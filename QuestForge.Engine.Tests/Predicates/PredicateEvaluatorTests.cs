using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Predicates;
using QuestForge.Predicates;
using QuestForge.Schema;
using Xunit;

namespace QuestForge.Engine.Tests.Predicates;

/// <summary>
/// Unit tests for PredicateEvaluator and ExpectEvaluator.
///
/// RED PHASE: All tests will throw NotImplementedException from the stub
/// PredicateEvaluator.Evaluate(PredicateAst, CancellationToken).
/// </summary>
public sealed class PredicateEvaluatorTests
{
    // -------------------------------------------------------------------------
    // §5.2.1 — questSequence integer comparisons
    // -------------------------------------------------------------------------

    [Fact]
    public async Task QuestSequence_AtZero_Gte255_ReturnsFalse()
    {
        /*
         * RED: NotImplementedException from PredicateEvaluator.Evaluate
         *
         * CONTRACT: Given questSequence(66130) == 0, When evaluating "questSequence(66130) >= 255",
         *           Then result is false.
         *
         * BUILDER GUIDANCE: Evaluate FunctionCall("questSequence", [IntLiteral(66130)])
         *                   → call IQuestState.GetQuestSequence(new QuestId(66130), ct)
         *                   → compare returned int with 255 using GtEq operator.
         */

        // Arrange
        var questState = new FakeQuestState();
        questState.SetQuestSequence(new QuestId(66130), 0);
        var evaluator = CreateEvaluator(new FakeGameStateProvider(), questState);
        var ast = PredicateParser.Parse("questSequence(66130) >= 255").Ast!;

        // Act
        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        // Assert
        Assert.False(result, "questSequence(66130)=0 should not satisfy >= 255");
    }

    [Fact]
    public async Task QuestSequence_AtZero_GteZero_ReturnsTrue()
    {
        /*
         * RED: NotImplementedException
         *
         * CONTRACT: Given questSequence(66130) == 0, When evaluating "questSequence(66130) >= 0",
         *           Then result is true.
         */

        // Arrange
        var questState = new FakeQuestState();
        questState.SetQuestSequence(new QuestId(66130), 0);
        var evaluator = CreateEvaluator(new FakeGameStateProvider(), questState);
        var ast = PredicateParser.Parse("questSequence(66130) >= 0").Ast!;

        // Act
        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        // Assert
        Assert.True(result, "questSequence(66130)=0 should satisfy >= 0");
    }

    [Fact]
    public async Task QuestSequence_At255_Gte255_ReturnsTrue()
    {
        /*
         * RED: NotImplementedException
         *
         * CONTRACT: Given questSequence(66130) == 255, When evaluating "questSequence(66130) >= 255",
         *           Then result is true.
         */

        // Arrange
        var questState = new FakeQuestState();
        questState.SetQuestSequence(new QuestId(66130), 255);
        var evaluator = CreateEvaluator(new FakeGameStateProvider(), questState);
        var ast = PredicateParser.Parse("questSequence(66130) >= 255").Ast!;

        // Act
        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        // Assert
        Assert.True(result, "questSequence(66130)=255 should satisfy >= 255");
    }

    [Fact]
    public async Task QuestSequence_At255_Eq255_ReturnsTrue()
    {
        /*
         * RED: NotImplementedException
         *
         * CONTRACT: Given questSequence(66130) == 255, When evaluating "questSequence(66130) == 255",
         *           Then result is true.
         */

        // Arrange
        var questState = new FakeQuestState();
        questState.SetQuestSequence(new QuestId(66130), 255);
        var evaluator = CreateEvaluator(new FakeGameStateProvider(), questState);
        var ast = PredicateParser.Parse("questSequence(66130) == 255").Ast!;

        // Act
        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        // Assert
        Assert.True(result, "questSequence(66130)=255 should satisfy == 255");
    }

    // -------------------------------------------------------------------------
    // §5.2.2 — isQuestComplete
    // -------------------------------------------------------------------------

    [Fact]
    public async Task IsQuestComplete_QuestAccepted_ReturnsFalse()
    {
        /*
         * RED: NotImplementedException
         *
         * CONTRACT: Given quest status is Accepted (not Complete),
         *           When evaluating "isQuestComplete(66130)", Then result is false.
         *
         * BUILDER GUIDANCE: isQuestComplete → IQuestState.IsQuestComplete(QuestId(66130))
         */

        // Arrange
        var questState = new FakeQuestState();
        questState.SetQuestStatus(new QuestId(66130), QuestStatus.Accepted);
        var evaluator = CreateEvaluator(new FakeGameStateProvider(), questState);
        var ast = PredicateParser.Parse("isQuestComplete(66130)").Ast!;

        // Act
        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        // Assert
        Assert.False(result, "Quest is Accepted, not Complete — should be false");
    }

    [Fact]
    public async Task IsQuestComplete_QuestComplete_ReturnsTrue()
    {
        /*
         * RED: NotImplementedException
         *
         * CONTRACT: Given quest status is Complete,
         *           When evaluating "isQuestComplete(66130)", Then result is true.
         */

        // Arrange
        var questState = new FakeQuestState();
        questState.SetQuestStatus(new QuestId(66130), QuestStatus.Complete);
        var evaluator = CreateEvaluator(new FakeGameStateProvider(), questState);
        var ast = PredicateParser.Parse("isQuestComplete(66130)").Ast!;

        // Act
        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        // Assert
        Assert.True(result, "Quest is Complete — should be true");
    }

    // -------------------------------------------------------------------------
    // §5.2.3 — playerZone
    // -------------------------------------------------------------------------

    [Fact]
    public async Task PlayerZone_Zone182_Eq182_ReturnsTrue()
    {
        /*
         * RED: NotImplementedException
         *
         * CONTRACT: Given player is in zone 182, When evaluating "playerZone() == 182",
         *           Then result is true.
         *
         * BUILDER GUIDANCE: playerZone() → IGameStateProvider.GetPlayerZone() → ZoneId.Value as long
         */

        // Arrange
        var gameState = new FakeGameStateProvider();
        gameState.SetZone(new ZoneId(182));
        var evaluator = CreateEvaluator(gameState, new FakeQuestState());
        var ast = PredicateParser.Parse("playerZone() == 182").Ast!;

        // Act
        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        // Assert
        Assert.True(result, "Player is in zone 182, predicate checks == 182");
    }

    [Fact]
    public async Task PlayerZone_Zone130_Eq182_ReturnsFalse()
    {
        /*
         * RED: NotImplementedException
         *
         * CONTRACT: Given player is in zone 130, When evaluating "playerZone() == 182",
         *           Then result is false.
         */

        // Arrange
        var gameState = new FakeGameStateProvider();
        gameState.SetZone(new ZoneId(130));
        var evaluator = CreateEvaluator(gameState, new FakeQuestState());
        var ast = PredicateParser.Parse("playerZone() == 182").Ast!;

        // Act
        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        // Assert
        Assert.False(result, "Player is in zone 130, not 182 — should be false");
    }

    // -------------------------------------------------------------------------
    // §5.2.4 — playerNear
    // -------------------------------------------------------------------------

    [Fact]
    public async Task PlayerNear_AtExactWymondPosition_ReturnsTrue()
    {
        /*
         * RED: NotImplementedException
         *
         * CONTRACT: Given player is at (35.56, 4.0, -151.18),
         *           When evaluating playerNear({"x":35.56,"y":4.0,"z":-151.18}, 3),
         *           Then result is true (distance ≈ 0, well within radius 3).
         *
         * BUILDER GUIDANCE: playerNear evaluates PositionLiteral to WorldPosition,
         *                   reads GetPlayerPosition(), computes Euclidean distance.
         */

        // Arrange
        var gameState = new FakeGameStateProvider();
        gameState.SetPosition(new WorldPosition(35.56f, 4.0f, -151.18f));
        var evaluator = CreateEvaluator(gameState, new FakeQuestState());
        var ast = PredicateParser.Parse(@"playerNear({""x"":35.56,""y"":4.0,""z"":-151.18}, 3)").Ast!;

        // Act
        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        // Assert
        Assert.True(result, "Player is at the exact position — distance is 0, within radius 3");
    }

    [Fact]
    public async Task PlayerNear_AtOrigin_FarFromWymond_ReturnsFalse()
    {
        /*
         * RED: NotImplementedException
         *
         * CONTRACT: Given player is at (0, 0, 0),
         *           When evaluating playerNear({"x":35.56,"y":4.0,"z":-151.18}, 3),
         *           Then result is false (distance >> 3).
         */

        // Arrange
        var gameState = new FakeGameStateProvider();
        gameState.SetPosition(new WorldPosition(0f, 0f, 0f));
        var evaluator = CreateEvaluator(gameState, new FakeQuestState());
        var ast = PredicateParser.Parse(@"playerNear({""x"":35.56,""y"":4.0,""z"":-151.18}, 3)").Ast!;

        // Act
        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        // Assert
        Assert.False(result, "Player is at origin, Wymond is at ~155 units away — not within radius 3");
    }

    // -------------------------------------------------------------------------
    // §5.2.5 — questFlag
    // -------------------------------------------------------------------------

    [Fact]
    public async Task QuestFlag_Bit1Set_ReturnsTrue()
    {
        /*
         * RED: NotImplementedException
         *
         * CONTRACT: Given flag bit 1 is set for quest 66130,
         *           When evaluating "questFlag(66130, 1)", Then result is true.
         *
         * BUILDER GUIDANCE: questFlag → IQuestState.IsQuestFlagSet(QuestId, flagBit)
         */

        // Arrange
        var questState = new FakeQuestState();
        questState.SetQuestFlagBit(new QuestId(66130), 1, true);
        var evaluator = CreateEvaluator(new FakeGameStateProvider(), questState);
        var ast = PredicateParser.Parse("questFlag(66130, 1)").Ast!;

        // Act
        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        // Assert
        Assert.True(result, "Flag bit 1 is set — should return true");
    }

    [Fact]
    public async Task QuestFlag_Bit1Unset_ReturnsFalse()
    {
        /*
         * RED: NotImplementedException
         *
         * CONTRACT: Given flag bit 1 is not set for quest 66130,
         *           When evaluating "questFlag(66130, 1)", Then result is false.
         */

        // Arrange
        var questState = new FakeQuestState();
        // Bit 1 is not set — default is 0
        var evaluator = CreateEvaluator(new FakeGameStateProvider(), questState);
        var ast = PredicateParser.Parse("questFlag(66130, 1)").Ast!;

        // Act
        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        // Assert
        Assert.False(result, "Flag bit 1 is not set — should return false");
    }

    // -------------------------------------------------------------------------
    // §5.2.6 — Logical operators: and, or, not
    // -------------------------------------------------------------------------

    [Fact]
    public async Task LogicalAnd_TrueAndFalse_ReturnsFalse()
    {
        /*
         * RED: NotImplementedException
         *
         * CONTRACT: Given playerZone() == 182 is true AND questSequence(66130) >= 255 is false,
         *           When evaluating "playerZone() == 182 and questSequence(66130) >= 255",
         *           Then result is false (AND short-circuits).
         *
         * BUILDER GUIDANCE: And node: evaluate left, short-circuit if false, evaluate right.
         */

        // Arrange
        var gameState = new FakeGameStateProvider();
        gameState.SetZone(new ZoneId(182));
        var questState = new FakeQuestState();
        questState.SetQuestSequence(new QuestId(66130), 0); // 0 < 255 → false
        var evaluator = CreateEvaluator(gameState, questState);
        var ast = PredicateParser.Parse("playerZone() == 182 and questSequence(66130) >= 255").Ast!;

        // Act
        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        // Assert
        Assert.False(result, "true AND false should be false");
    }

    [Fact]
    public async Task LogicalOr_TrueOrFalse_ReturnsTrue()
    {
        /*
         * RED: NotImplementedException
         *
         * CONTRACT: Given playerZone() == 182 is true OR questSequence(66130) >= 255 is false,
         *           When evaluating "playerZone() == 182 or questSequence(66130) >= 255",
         *           Then result is true (OR short-circuits on first true).
         *
         * BUILDER GUIDANCE: Or node: evaluate left, short-circuit if true, evaluate right.
         */

        // Arrange
        var gameState = new FakeGameStateProvider();
        gameState.SetZone(new ZoneId(182));
        var questState = new FakeQuestState();
        questState.SetQuestSequence(new QuestId(66130), 0);
        var evaluator = CreateEvaluator(gameState, questState);
        var ast = PredicateParser.Parse("playerZone() == 182 or questSequence(66130) >= 255").Ast!;

        // Act
        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        // Assert
        Assert.True(result, "true OR false should be true");
    }

    [Fact]
    public async Task LogicalNot_FalsePredicate_ReturnsTrue()
    {
        /*
         * RED: NotImplementedException
         *
         * CONTRACT: Given questSequence(66130) >= 255 is false,
         *           When evaluating "not (questSequence(66130) >= 255)",
         *           Then result is true.
         *
         * BUILDER GUIDANCE: Not node: evaluate inner, negate.
         */

        // Arrange
        var questState = new FakeQuestState();
        questState.SetQuestSequence(new QuestId(66130), 0);
        var evaluator = CreateEvaluator(new FakeGameStateProvider(), questState);
        var ast = PredicateParser.Parse("not (questSequence(66130) >= 255)").Ast!;

        // Act
        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        // Assert
        Assert.True(result, "not (false) should be true");
    }

    // -------------------------------------------------------------------------
    // §5.2.7 — ExpectValue.AllExpect (via ExpectEvaluator)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task AllExpect_BothPredicatesTrue_ReturnsTrue()
    {
        /*
         * RED: NotImplementedException from ExpectEvaluator.Evaluate(ExpectValue?, ct)
         *
         * CONTRACT: Given two predicates both evaluating to true,
         *           When evaluating AllExpect, Then result is true.
         *
         * BUILDER GUIDANCE: AllExpect.All: parse each string, evaluate all, short-circuit AND.
         */

        // Arrange
        var gameState = new FakeGameStateProvider();
        gameState.SetZone(new ZoneId(182));
        var questState = new FakeQuestState();
        questState.SetQuestSequence(new QuestId(66130), 255);
        var expectEval = CreateExpectEvaluator(gameState, questState);

        var expect = new AllExpect
        {
            All = ["playerZone() == 182", "questSequence(66130) >= 255"]
        };

        // Act
        var result = await expectEval.Evaluate(expect, CancellationToken.None);

        // Assert
        Assert.True(result, "All predicates are true — AllExpect should be true");
    }

    [Fact]
    public async Task AllExpect_OneTrueOneFalse_ReturnsFalse()
    {
        /*
         * RED: NotImplementedException
         *
         * CONTRACT: Given one true and one false predicate,
         *           When evaluating AllExpect, Then result is false.
         */

        // Arrange
        var gameState = new FakeGameStateProvider();
        gameState.SetZone(new ZoneId(182));
        var questState = new FakeQuestState();
        questState.SetQuestSequence(new QuestId(66130), 0); // 0 >= 255 → false
        var expectEval = CreateExpectEvaluator(gameState, questState);

        var expect = new AllExpect
        {
            All = ["playerZone() == 182", "questSequence(66130) >= 255"]
        };

        // Act
        var result = await expectEval.Evaluate(expect, CancellationToken.None);

        // Assert
        Assert.False(result, "One predicate is false — AllExpect should be false");
    }

    // -------------------------------------------------------------------------
    // §5.2.8 — ExpectValue.AnyExpect (via ExpectEvaluator)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task AnyExpect_BothPredicatesFalse_ReturnsFalse()
    {
        /*
         * RED: NotImplementedException
         *
         * CONTRACT: Given two predicates both evaluating to false,
         *           When evaluating AnyExpect, Then result is false.
         *
         * BUILDER GUIDANCE: AnyExpect.Any: parse each string, short-circuit OR.
         */

        // Arrange
        var gameState = new FakeGameStateProvider();
        gameState.SetZone(new ZoneId(130)); // not 182
        var questState = new FakeQuestState();
        questState.SetQuestSequence(new QuestId(66130), 0); // 0 >= 255 → false
        var expectEval = CreateExpectEvaluator(gameState, questState);

        var expect = new AnyExpect
        {
            Any = ["playerZone() == 182", "questSequence(66130) >= 255"]
        };

        // Act
        var result = await expectEval.Evaluate(expect, CancellationToken.None);

        // Assert
        Assert.False(result, "Both predicates are false — AnyExpect should be false");
    }

    [Fact]
    public async Task AnyExpect_OneTrueOneFalse_ReturnsTrue()
    {
        /*
         * RED: NotImplementedException
         *
         * CONTRACT: Given one predicate true and one false,
         *           When evaluating AnyExpect, Then result is true.
         */

        // Arrange
        var gameState = new FakeGameStateProvider();
        gameState.SetZone(new ZoneId(182)); // matches == 182
        var questState = new FakeQuestState();
        questState.SetQuestSequence(new QuestId(66130), 0); // 0 >= 255 → false
        var expectEval = CreateExpectEvaluator(gameState, questState);

        var expect = new AnyExpect
        {
            Any = ["playerZone() == 182", "questSequence(66130) >= 255"]
        };

        // Act
        var result = await expectEval.Evaluate(expect, CancellationToken.None);

        // Assert
        Assert.True(result, "One predicate is true — AnyExpect should be true");
    }

    // -------------------------------------------------------------------------
    // §5.2.9 — Error cases
    // -------------------------------------------------------------------------

    [Fact]
    public async Task UnknownFunction_ThrowsUnknownStateFunctionException()
    {
        /*
         * RED: NotImplementedException
         *
         * CONTRACT: Given a predicate referencing unknown function "foo()",
         *           When evaluating, Then UnknownStateFunctionException is thrown
         *           with the function name in the message.
         *
         * BUILDER GUIDANCE: The evaluator's function dispatch table has no entry for "foo".
         *                   Throw UnknownStateFunctionException(functionName) immediately.
         *                   Do NOT use Result<T> — this is a quest data bug, not a routine failure.
         */

        // Arrange
        var evaluator = CreateEvaluator(new FakeGameStateProvider(), new FakeQuestState());

        // The parser may or may not reject "foo()" — if it parses, the evaluator must reject it.
        // If the parser rejects it, we construct the AST node directly.
        var ast = new PredicateAst.FunctionCall("foo", Array.Empty<PredicateAst>());

        // Act & Assert
        var ex = await Assert.ThrowsAsync<UnknownStateFunctionException>(
            () => evaluator.Evaluate(ast, CancellationToken.None));

        Assert.Contains("foo", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BareIntPredicate_ViaExpectEvaluator_ThrowsEnginePredicateShapeException()
    {
        /*
         * RED: NotImplementedException
         *
         * CONTRACT: Given a predicate that evaluates to int at the top level
         *           (e.g., bare "questSequence(66130)" — returns int, not bool),
         *           When evaluating via ExpectEvaluator, Then EnginePredicateShapeException is thrown.
         *
         * BUILDER GUIDANCE: The top-level result of Evaluate(PredicateAst) must be bool.
         *                   If it resolves to a non-bool type (int, string, position),
         *                   throw EnginePredicateShapeException.
         *                   PredicateExpect wrapping "questSequence(66130)" produces this situation.
         */

        // Arrange
        var questState = new FakeQuestState();
        questState.SetQuestSequence(new QuestId(66130), 0);
        var expectEval = CreateExpectEvaluator(new FakeGameStateProvider(), questState);

        // "questSequence(66130)" returns int — not bool — at the top level
        var expect = new PredicateExpect { Predicate = "questSequence(66130)" };

        // Act & Assert
        await Assert.ThrowsAsync<EnginePredicateShapeException>(
            () => expectEval.Evaluate(expect, CancellationToken.None));
    }

    // =========================================================================
    // Phase 11B — isAttuned predicate tests (B1-B5 from PHASE_11B_PLAN.md §3.3)
    // =========================================================================

    [Fact]
    public async Task IsAttuned_NotAttuned_ReturnsFalse()
    {
        /*
         * RED: Will fail until Builder implements the isAttuned switch arm.
         *
         * CONTRACT: Given FakeGameStateProvider with no SetAetheryteAttuned call for ID 1000,
         *           When evaluator evaluates "isAttuned(1000)",
         *           Then result is false.
         *
         * BUILDER GUIDANCE: Add "isAttuned" arm to EvaluateFunction:
         *   "isAttuned" => (await _gameState.IsAetheryteAttuned(
         *       new AetheryteId((uint)(long)args[0]), ct)).ValueOrThrow,
         *   Note: args[0] is a boxed long (IntLiteral evaluates to long).
         */

        // Arrange
        var gameState = new FakeGameStateProvider();
        // No SetAetheryteAttuned call — default is false
        var evaluator = CreateEvaluator(gameState, new FakeQuestState());
        var ast = PredicateParser.Parse("isAttuned(1000)").Ast!;

        // Act
        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        // Assert
        Assert.False(result, "Aetheryte 1000 is not attuned — isAttuned(1000) should be false");
    }

    [Fact]
    public async Task IsAttuned_Attuned_ReturnsTrue()
    {
        /*
         * RED: Will fail until Builder implements the isAttuned switch arm.
         *
         * CONTRACT: Given gameState.SetAetheryteAttuned(new AetheryteId(1000), true),
         *           When evaluator evaluates "isAttuned(1000)",
         *           Then result is true.
         */

        // Arrange
        var gameState = new FakeGameStateProvider();
        gameState.SetAetheryteAttuned(new QuestForge.Adapters.Types.AetheryteId(1000), true);
        var evaluator = CreateEvaluator(gameState, new FakeQuestState());
        var ast = PredicateParser.Parse("isAttuned(1000)").Ast!;

        // Act
        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        // Assert
        Assert.True(result, "Aetheryte 1000 was set attuned — isAttuned(1000) should be true");
    }

    [Fact]
    public async Task IsAttuned_StateFlip_ObservableWithoutCaching()
    {
        /*
         * RED: Will fail until Builder implements the isAttuned switch arm.
         *
         * CONTRACT: Given initial state not attuned (B1 scenario),
         *           When SetAetheryteAttuned(1000, true) is called and then re-evaluated,
         *           Then the second evaluation returns true.
         *           (Validates no internal caching of predicate results.)
         *
         * BUILDER GUIDANCE: The ExpectEvaluator caches parse trees, not values.
         *   The evaluator re-reads IGameStateProvider on every call.
         */

        // Arrange
        var gameState = new FakeGameStateProvider();
        var evaluator = CreateEvaluator(gameState, new FakeQuestState());
        var ast = PredicateParser.Parse("isAttuned(1000)").Ast!;

        // Act — first evaluation: not attuned
        var firstResult = await evaluator.Evaluate(ast, CancellationToken.None);

        // Now flip the state
        gameState.SetAetheryteAttuned(new QuestForge.Adapters.Types.AetheryteId(1000), true);

        // Second evaluation: now attuned
        var secondResult = await evaluator.Evaluate(ast, CancellationToken.None);

        // Assert
        Assert.False(firstResult, "Before SetAetheryteAttuned — should be false");
        Assert.True(secondResult, "After SetAetheryteAttuned(true) — should be true (no caching)");
    }

    [Fact]
    public async Task IsAttuned_ComposedInNot_InvertsResult()
    {
        /*
         * RED: Will fail until Builder implements the isAttuned switch arm.
         *
         * CONTRACT: Given isAttuned(1000) == false (aetheryte not attuned),
         *           When evaluator parses and evaluates "not isAttuned(1000)",
         *           Then result is true.
         *
         * BUILDER GUIDANCE: The Not node delegates to EvaluateInternal(inner) — no extra work needed
         *   once the isAttuned arm returns a bool.
         */

        // Arrange
        var gameState = new FakeGameStateProvider();
        // Not attuned — isAttuned(1000) == false
        var evaluator = CreateEvaluator(gameState, new FakeQuestState());
        var ast = PredicateParser.Parse("not isAttuned(1000)").Ast!;

        // Act
        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        // Assert
        Assert.True(result, "not isAttuned(1000) where 1000 is not attuned — should be true");
    }

    [Fact]
    public async Task IsAttuned_ComposedInAnd_ShortCircuitsCorrectly()
    {
        /*
         * RED: Will fail until Builder implements the isAttuned switch arm.
         *
         * CONTRACT: Given questSequence(66130) == 0 (>= 0 is true) AND isAttuned(1000) == false,
         *           When evaluator evaluates "questSequence(66130) >= 0 and isAttuned(1000)",
         *           Then result is false (and short-circuits after second operand).
         *
         * BUILDER GUIDANCE: Tests that isAttuned integrates with the And short-circuit logic.
         */

        // Arrange
        var gameState = new FakeGameStateProvider();
        // Not attuned
        var questState = new FakeQuestState();
        questState.SetQuestSequence(new QuestId(66130), 0);
        var evaluator = CreateEvaluator(gameState, questState);
        var ast = PredicateParser.Parse("questSequence(66130) >= 0 and isAttuned(1000)").Ast!;

        // Act
        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        // Assert
        Assert.False(result, "questSequence(66130)>=0 (true) AND isAttuned(1000) (false) = false");
    }

    // === playerHasItem predicate tests ===

    [Fact]
    public async Task PlayerHasItem_ItemNotPresent_ReturnsFalse()
    {
        /*
         * RED: Will fail until Builder implements the playerHasItem switch arm.
         *
         * CONTRACT: Given no SetItemCount call for item 2000386,
         *           When evaluating "playerHasItem(2000386)",
         *           Then result is false (default count is 0, which is < 1).
         *
         * BUILDER GUIDANCE: Add "playerHasItem" arm to EvaluateFunction:
         *   "playerHasItem" when args.Count == 1 =>
         *       (await _gameState.GetItemCount(new ItemId((uint)(long)args[0]), ct)).ValueOrThrow >= 1
         */

        // Arrange
        var gameState = new FakeGameStateProvider();
        // No SetItemCount call — default is 0
        var evaluator = CreateEvaluator(gameState, new FakeQuestState());
        var ast = PredicateParser.Parse("playerHasItem(2000386)").Ast!;

        // Act
        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        // Assert
        Assert.False(result, "Item 2000386 count is 0 — playerHasItem(2000386) should be false");
    }

    [Fact]
    public async Task PlayerHasItem_ItemCountOne_ReturnsTrue()
    {
        /*
         * RED: Will fail until Builder implements the playerHasItem switch arm.
         *
         * CONTRACT: Given gameState.SetItemCount(new ItemId(2000386), 1),
         *           When evaluating "playerHasItem(2000386)",
         *           Then result is true (count 1 >= 1).
         */

        // Arrange
        var gameState = new FakeGameStateProvider();
        gameState.SetItemCount(new ItemId(2000386), 1);
        var evaluator = CreateEvaluator(gameState, new FakeQuestState());
        var ast = PredicateParser.Parse("playerHasItem(2000386)").Ast!;

        // Act
        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        // Assert
        Assert.True(result, "Item 2000386 count is 1 — playerHasItem(2000386) should be true");
    }

    [Fact]
    public async Task PlayerHasItem_ItemCountGreaterThanOne_ReturnsTrue()
    {
        /*
         * RED: Will fail until Builder implements the playerHasItem switch arm.
         *
         * CONTRACT: Given gameState.SetItemCount(new ItemId(2000386), 5),
         *           When evaluating "playerHasItem(2000386)",
         *           Then result is true (count 5 >= 1).
         */

        // Arrange
        var gameState = new FakeGameStateProvider();
        gameState.SetItemCount(new ItemId(2000386), 5);
        var evaluator = CreateEvaluator(gameState, new FakeQuestState());
        var ast = PredicateParser.Parse("playerHasItem(2000386)").Ast!;

        // Act
        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        // Assert
        Assert.True(result, "Item 2000386 count is 5 — playerHasItem(2000386) should be true");
    }

    [Fact]
    public async Task PlayerHasItem_NegatedWhenItemAbsent_ReturnsTrue()
    {
        /*
         * RED: Will fail until Builder implements the playerHasItem switch arm.
         *
         * CONTRACT: Given no SetItemCount call for item 2000386 (count is 0),
         *           When evaluating "not(playerHasItem(2000386))",
         *           Then result is true — the handover postcondition pattern where
         *           a step's completion is confirmed by the item no longer being held.
         *
         * BUILDER GUIDANCE: The Not node delegates to EvaluateInternal(inner) — no extra work
         *   needed once the playerHasItem arm returns a bool.
         */

        // Arrange
        var gameState = new FakeGameStateProvider();
        // No item — playerHasItem(2000386) == false, so not(...) == true
        var evaluator = CreateEvaluator(gameState, new FakeQuestState());
        var ast = PredicateParser.Parse("not(playerHasItem(2000386))").Ast!;

        // Act
        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        // Assert
        Assert.True(result, "not(playerHasItem(2000386)) where item is absent — should be true");
    }

    [Fact]
    public async Task PlayerHasItem_ExplicitZero_ReturnsFalse()
    {
        /*
         * RED: Will fail until Builder implements the playerHasItem switch arm.
         *
         * CONTRACT: Given gameState.SetItemCount(new ItemId(2000386), 0),
         *           When evaluating "playerHasItem(2000386)",
         *           Then result is false (0 < 1).
         */

        // Arrange
        var gameState = new FakeGameStateProvider();
        gameState.SetItemCount(new ItemId(2000386), 0);
        var evaluator = CreateEvaluator(gameState, new FakeQuestState());
        var ast = PredicateParser.Parse("playerHasItem(2000386)").Ast!;

        // Act
        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        // Assert
        Assert.False(result, "Item 2000386 count explicitly set to 0 — playerHasItem(2000386) should be false");
    }

    [Fact]
    public async Task PlayerHasItem_StateFlip_ObservableWithoutCaching()
    {
        /*
         * RED: Will fail until Builder implements the playerHasItem switch arm.
         *
         * CONTRACT: Given initial count of 0 (first evaluation returns false),
         *           When SetItemCount(new ItemId(2000386), 1) is called and then re-evaluated,
         *           Then the second evaluation returns true.
         *           (Validates no internal caching of predicate results.)
         *
         * BUILDER GUIDANCE: The evaluator re-reads IGameStateProvider on every call.
         *   The ExpectEvaluator caches parse trees, not values.
         */

        // Arrange
        var gameState = new FakeGameStateProvider();
        var evaluator = CreateEvaluator(gameState, new FakeQuestState());
        var ast = PredicateParser.Parse("playerHasItem(2000386)").Ast!;

        // Act — first evaluation: item absent
        var firstResult = await evaluator.Evaluate(ast, CancellationToken.None);

        // Now flip the state
        gameState.SetItemCount(new ItemId(2000386), 1);

        // Second evaluation: item now present
        var secondResult = await evaluator.Evaluate(ast, CancellationToken.None);

        // Assert
        Assert.False(firstResult, "Before SetItemCount — should be false");
        Assert.True(secondResult, "After SetItemCount(1) — should be true (no caching)");
    }

    [Fact]
    public async Task PlayerHasItem_ItemIdZero_DispatchedToAdapter_ReturnsFalse()
    {
        /*
         * RED: Will fail until Builder implements the playerHasItem switch arm.
         *
         * CONTRACT: Given no SetItemCount call for item 0,
         *           When evaluating "playerHasItem(0)",
         *           Then result is false (engine does not special-case itemId=0;
         *           the adapter returns 0 and 0 >= 1 is false).
         *
         * BUILDER GUIDANCE: Do not add any special-case for itemId == 0 in the switch arm.
         *   Dispatch directly to GetItemCount — the adapter's default-zero behaviour handles it.
         */

        // Arrange
        var gameState = new FakeGameStateProvider();
        // No SetItemCount for ItemId(0) — default is 0
        var evaluator = CreateEvaluator(gameState, new FakeQuestState());
        var ast = PredicateParser.Parse("playerHasItem(0)").Ast!;

        // Act
        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        // Assert
        Assert.False(result, "Item 0 count is 0 — playerHasItem(0) should be false, not a special case");
    }

    [Fact]
    public async Task PlayerHasItem_ComposedInAnd_EvaluatesCorrectly()
    {
        /*
         * RED: Will fail until Builder implements the playerHasItem switch arm.
         *
         * CONTRACT: Given questSequence(66130) == 50 (>= 0 is true)
         *           AND gameState.SetItemCount(new ItemId(2000386), 1),
         *           When evaluating "questSequence(66130) >= 0 and playerHasItem(2000386)",
         *           Then result is true (both operands are true).
         *
         * BUILDER GUIDANCE: Tests that playerHasItem integrates with the And node's
         *   short-circuit logic without interference.
         */

        // Arrange
        var gameState = new FakeGameStateProvider();
        gameState.SetItemCount(new ItemId(2000386), 1);
        var questState = new FakeQuestState();
        questState.SetQuestSequence(new QuestId(66130), 50);
        var evaluator = CreateEvaluator(gameState, questState);
        var ast = PredicateParser.Parse("questSequence(66130) >= 0 and playerHasItem(2000386)").Ast!;

        // Act
        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        // Assert
        Assert.True(result, "questSequence(66130)>=0 (true) AND playerHasItem(2000386) (true) = true");
    }

    [Fact]
    public async Task PlayerHasItem_TwoArgForm_ReturnsTrueWhenCountMeetsQuantity()
    {
        /*
         * Updated by Slice B (purchase-item-engine): the two-arg form playerHasItem(itemId, qty)
         * is now supported and returns true when GetItemCount >= qty.
         * The previous contract (throw UnknownStateFunctionException) was superseded when
         * PurchaseItemStep synthesis began emitting playerHasItem(id,qty) predicates.
         *
         * CONTRACT: playerHasItem(2000386, 3) with count=3 → true; count=2 → false.
         */

        // Arrange
        var gameState = new FakeGameStateProvider();
        var evaluator = CreateEvaluator(gameState, new FakeQuestState());

        var ast = new PredicateAst.FunctionCall(
            "playerHasItem",
            new PredicateAst[] { new PredicateAst.IntLiteral(2000386), new PredicateAst.IntLiteral(3) });

        // count=3 meets qty=3 → true
        gameState.SetItemCount(new ItemId(2000386), 3);
        Assert.True(await evaluator.Evaluate(ast, CancellationToken.None));

        // count=2 does not meet qty=3 → false
        gameState.SetItemCount(new ItemId(2000386), 2);
        Assert.False(await evaluator.Evaluate(ast, CancellationToken.None));
    }

    // -------------------------------------------------------------------------
    // Factory helpers
    // -------------------------------------------------------------------------

    private static PredicateEvaluator CreateEvaluator(
        FakeGameStateProvider gameState,
        FakeQuestState questState)
        => new PredicateEvaluator(gameState, questState);

    private static ExpectEvaluator CreateExpectEvaluator(
        FakeGameStateProvider gameState,
        FakeQuestState questState)
    {
        var inner = new PredicateEvaluator(gameState, questState);
        return new ExpectEvaluator(inner);
    }
}