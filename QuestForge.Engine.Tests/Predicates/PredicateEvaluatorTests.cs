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

    // =========================================================================
    // playerHasEquipped predicate tests (T1-T8 from IS_ITEM_EQUIPPED_PREDICATE_PLAN.md)
    // =========================================================================

    [Fact]
    public async Task PlayerHasEquipped_ItemEquipped_ReturnsTrue()
    {
        /*
         * T1: Happy path — item IS equipped, predicate returns true.
         *
         * CONTRACT: Given FakeGameStateProvider has item 4567 equipped,
         *           When evaluating "playerHasEquipped(4567)",
         *           Then result is true.
         *
         * BUILDER GUIDANCE: Add "playerHasEquipped" arm to EvaluateFunction:
         *   "playerHasEquipped" when args.Length == 1 =>
         *       (await _gameState.IsItemEquipped(new ItemId((uint)(long)args[0]), ct)).ValueOrThrow,
         */

        // Arrange
        var gameState = new FakeGameStateProvider();
        gameState.SetItemEquipped(new ItemId(4567), true);
        var evaluator = CreateEvaluator(gameState, new FakeQuestState());
        var ast = PredicateParser.Parse("playerHasEquipped(4567)").Ast!;

        // Act
        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        // Assert
        Assert.True(result, "Item 4567 is equipped — playerHasEquipped(4567) should be true");
    }

    [Fact]
    public async Task PlayerHasEquipped_ItemNotEquipped_ReturnsFalse()
    {
        /*
         * T2: Happy path — item is NOT equipped, predicate returns false.
         *
         * CONTRACT: Given FakeGameStateProvider has NO items equipped,
         *           When evaluating "playerHasEquipped(4567)",
         *           Then result is false.
         */

        // Arrange
        var gameState = new FakeGameStateProvider();
        // No SetItemEquipped call — default is false
        var evaluator = CreateEvaluator(gameState, new FakeQuestState());
        var ast = PredicateParser.Parse("playerHasEquipped(4567)").Ast!;

        // Act
        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        // Assert
        Assert.False(result, "Item 4567 is not equipped — playerHasEquipped(4567) should be false");
    }

    [Fact]
    public async Task PlayerHasEquipped_Negation_ReturnsFalse()
    {
        /*
         * T3: Negation works — not playerHasEquipped(4567) when item IS equipped returns false.
         *
         * CONTRACT: Given item 4567 is equipped,
         *           When evaluating "not playerHasEquipped(4567)",
         *           Then result is false.
         *
         * BUILDER GUIDANCE: The Not node delegates to EvaluateInternal(inner) — no extra work
         *   needed once the playerHasEquipped arm returns a bool.
         */

        // Arrange
        var gameState = new FakeGameStateProvider();
        gameState.SetItemEquipped(new ItemId(4567), true);
        var evaluator = CreateEvaluator(gameState, new FakeQuestState());
        var ast = PredicateParser.Parse("not playerHasEquipped(4567)").Ast!;

        // Act
        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        // Assert
        Assert.False(result, "not playerHasEquipped(4567) where item is equipped — should be false");
    }

    [Fact]
    public async Task PlayerHasEquipped_ComposedWithPlayerHasItem_BothTrue()
    {
        /*
         * T4: Composition — playerHasItem(4567) and playerHasEquipped(4567) both true.
         *
         * CONTRACT: Given item 4567 is in inventory (count 1) AND equipped,
         *           When evaluating "playerHasItem(4567) and playerHasEquipped(4567)",
         *           Then result is true.
         *
         * BUILDER GUIDANCE: Tests that playerHasEquipped integrates with the And node
         *   and co-exists with playerHasItem in the same expression.
         */

        // Arrange
        var gameState = new FakeGameStateProvider();
        gameState.SetItemCount(new ItemId(4567), 1);
        gameState.SetItemEquipped(new ItemId(4567), true);
        var evaluator = CreateEvaluator(gameState, new FakeQuestState());
        var ast = PredicateParser.Parse("playerHasItem(4567) and playerHasEquipped(4567)").Ast!;

        // Act
        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        // Assert
        Assert.True(result, "playerHasItem(4567) (true) AND playerHasEquipped(4567) (true) = true");
    }

    [Fact]
    public async Task PlayerHasEquipped_TwoArgForm_ThrowsNotSupportedException()
    {
        /*
         * T5: Two-arg form throws NotSupportedException.
         *
         * CONTRACT: Given any state,
         *           When evaluating "playerHasEquipped(4567, "mainhand")",
         *           Then throws NotSupportedException with message containing
         *           "slot filter is not yet implemented".
         *
         * BUILDER GUIDANCE: Add a second switch arm:
         *   "playerHasEquipped" when args.Length == 2 =>
         *       throw new NotSupportedException(
         *           "playerHasEquipped(itemId, slotName) slot filter is not yet implemented. Use playerHasEquipped(itemId)."),
         */

        // Arrange
        var gameState = new FakeGameStateProvider();
        var evaluator = CreateEvaluator(gameState, new FakeQuestState());

        // The parser allows the 2-arg form (OptionalTail(1,1)). Construct via AST to be safe.
        var ast = new PredicateAst.FunctionCall(
            "playerHasEquipped",
            new PredicateAst[] { new PredicateAst.IntLiteral(4567), new PredicateAst.StringLiteral("mainhand") });

        // Act & Assert
        var ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => evaluator.Evaluate(ast, CancellationToken.None));

        Assert.Contains("slot filter is not yet implemented", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlayerHasEquipped_InExpectPostcondition_PassesWhenEquipped()
    {
        /*
         * T6: Used in Expect postcondition — step completes when item is equipped.
         *
         * CONTRACT: Given a PredicateExpect with "playerHasEquipped(1234)" and item 1234 IS equipped,
         *           When ExpectEvaluator evaluates the postcondition,
         *           Then result is true (postcondition passes).
         */

        // Arrange
        var gameState = new FakeGameStateProvider();
        gameState.SetItemEquipped(new ItemId(1234), true);
        var expectEval = CreateExpectEvaluator(gameState, new FakeQuestState());
        var expect = new PredicateExpect { Predicate = "playerHasEquipped(1234)" };

        // Act
        var result = await expectEval.Evaluate(expect, CancellationToken.None);

        // Assert
        Assert.True(result, "Postcondition playerHasEquipped(1234) should pass when item is equipped");
    }

    [Fact]
    public async Task PlayerHasEquipped_InExpectPostcondition_FailsWhenNotEquipped()
    {
        /*
         * T7: Postcondition fails when item is NOT equipped.
         *
         * CONTRACT: Given a PredicateExpect with "playerHasEquipped(1234)" and item 1234 is NOT equipped,
         *           When ExpectEvaluator evaluates the postcondition,
         *           Then result is false (postcondition fails, engine retries).
         */

        // Arrange
        var gameState = new FakeGameStateProvider();
        // No SetItemEquipped — item not equipped
        var expectEval = CreateExpectEvaluator(gameState, new FakeQuestState());
        var expect = new PredicateExpect { Predicate = "playerHasEquipped(1234)" };

        // Act
        var result = await expectEval.Evaluate(expect, CancellationToken.None);

        // Assert
        Assert.False(result, "Postcondition playerHasEquipped(1234) should fail when item is not equipped");
    }

    [Fact]
    public async Task PlayerHasEquipped_SetItemEquippedToggle_ReflectsLatestState()
    {
        /*
         * T8: FakeGameStateProvider.SetItemEquipped toggle — set true then false, returns false.
         *
         * CONTRACT: Given SetItemEquipped(item, true) then SetItemEquipped(item, false),
         *           When IsItemEquipped(item) is called via the predicate,
         *           Then returns false.
         *           (Validates no internal caching and that the toggle removes the item.)
         */

        // Arrange
        var gameState = new FakeGameStateProvider();
        var evaluator = CreateEvaluator(gameState, new FakeQuestState());
        var ast = PredicateParser.Parse("playerHasEquipped(4567)").Ast!;

        // Set equipped, verify true
        gameState.SetItemEquipped(new ItemId(4567), true);
        var equippedResult = await evaluator.Evaluate(ast, CancellationToken.None);
        Assert.True(equippedResult, "After SetItemEquipped(true) — should be true");

        // Unequip, verify false
        gameState.SetItemEquipped(new ItemId(4567), false);
        var unequippedResult = await evaluator.Evaluate(ast, CancellationToken.None);
        Assert.False(unequippedResult, "After SetItemEquipped(false) — should be false");
    }

    // =========================================================================
    // isAetherCurrentAttuned predicate tests (IO-P1, IO-P2 from INTERACT_OBJECT_STEP_PLAN.md)
    // =========================================================================

    [Fact]
    public async Task IsAetherCurrentAttuned_Attuned_ReturnsTrue_IOP1()
    {
        /*
         * RED: Will fail until Builder implements the isAetherCurrentAttuned switch arm.
         *
         * CONTRACT: Given FakeGameStateProvider.SetAetherCurrentAttuned(2818048, true),
         *           When evaluating "isAetherCurrentAttuned(2818048)",
         *           Then result is true.
         *
         * BUILDER GUIDANCE: Add "isAetherCurrentAttuned" arm to EvaluateFunction:
         *   "isAetherCurrentAttuned" => (await _gameState.IsAetherCurrentAttuned(
         *       (uint)(long)args[0], ct)).ValueOrThrow,
         *   Also add IsAetherCurrentAttuned to IGameStateProvider, FakeGameStateProvider,
         *   and RecordingGameStateProvider.
         */

        // Arrange
        var gameState = new FakeGameStateProvider();
        gameState.SetAetherCurrentAttuned(2818048u, true);               // RED: method does not exist yet
        var evaluator = CreateEvaluator(gameState, new FakeQuestState());
        var ast = PredicateParser.Parse("isAetherCurrentAttuned(2818048)").Ast!;

        // Act
        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        // Assert
        Assert.True(result, "Aether current 2818048 is attuned — isAetherCurrentAttuned(2818048) should be true");
    }

    [Fact]
    public async Task IsAetherCurrentAttuned_NotAttuned_ReturnsFalse_IOP2()
    {
        /*
         * RED: Will fail until Builder implements the isAetherCurrentAttuned switch arm.
         *
         * CONTRACT: Given no aether currents set,
         *           When evaluating "isAetherCurrentAttuned(2818048)",
         *           Then result is false.
         */

        // Arrange
        var gameState = new FakeGameStateProvider();
        // No SetAetherCurrentAttuned call — default is false
        var evaluator = CreateEvaluator(gameState, new FakeQuestState());
        var ast = PredicateParser.Parse("isAetherCurrentAttuned(2818048)").Ast!;

        // Act
        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        // Assert
        Assert.False(result, "No aether currents set — isAetherCurrentAttuned(2818048) should be false");
    }

    // =========================================================================
    // npcExistsNearby predicate tests (IO-P3 through IO-P5 from INTERACT_OBJECT_STEP_PLAN.md)
    // =========================================================================

    [Fact]
    public async Task NpcExistsNearby_NpcExists_ReturnsTrue_IOP3()
    {
        /*
         * RED: Will fail until Builder implements the npcExistsNearby switch arm.
         *
         * CONTRACT: Given an NPC with NpcId(1000789) is nearby,
         *           When evaluating "npcExistsNearby(1000789)",
         *           Then result is true.
         *
         * BUILDER GUIDANCE: Add "npcExistsNearby" arm to EvaluateFunction:
         *   "npcExistsNearby" => await EvaluateNpcExistsNearby((long)args[0], ct),
         *   The helper tries FindNpc first, then FindInteractable.
         */

        // Arrange
        var gameState = new FakeGameStateProvider();
        gameState.AddNpc(new NpcReference(
            new NpcId(1000789),
            new WorldPosition(10f, 0f, 10f),
            DistanceToPlayer: 5f));
        var evaluator = CreateEvaluator(gameState, new FakeQuestState());
        var ast = PredicateParser.Parse("npcExistsNearby(1000789)").Ast!;

        // Act
        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        // Assert
        Assert.True(result, "NPC 1000789 exists — npcExistsNearby(1000789) should be true");
    }

    [Fact]
    public async Task NpcExistsNearby_InteractableExists_ReturnsTrue_IOP4()
    {
        /*
         * RED: Will fail until Builder implements the npcExistsNearby switch arm.
         *
         * CONTRACT: Given an interactable with InteractableId(2001234) is nearby (not an NPC),
         *           When evaluating "npcExistsNearby(2001234)",
         *           Then result is true (falls through NPC miss to interactable hit).
         */

        // Arrange
        var gameState = new FakeGameStateProvider();
        // No NPC with this ID, but an interactable exists
        gameState.AddInteractable(new InteractableReference(
            new InteractableId(2001234),
            new WorldPosition(5f, 0f, 5f),
            DistanceToPlayer: 3f,
            Kind: InteractableKind.EventTrigger));
        var evaluator = CreateEvaluator(gameState, new FakeQuestState());
        var ast = PredicateParser.Parse("npcExistsNearby(2001234)").Ast!;

        // Act
        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        // Assert
        Assert.True(result,
            "Interactable 2001234 exists (NPC miss, interactable hit) — npcExistsNearby(2001234) should be true");
    }

    [Fact]
    public async Task NpcExistsNearby_NeitherExists_ReturnsFalse_IOP5()
    {
        /*
         * RED: Will fail until Builder implements the npcExistsNearby switch arm.
         *
         * CONTRACT: Given no NPCs or interactables set,
         *           When evaluating "npcExistsNearby(9999999)",
         *           Then result is false.
         */

        // Arrange
        var gameState = new FakeGameStateProvider();
        // No NPCs, no interactables
        var evaluator = CreateEvaluator(gameState, new FakeQuestState());
        var ast = PredicateParser.Parse("npcExistsNearby(9999999)").Ast!;

        // Act
        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        // Assert
        Assert.False(result, "Nothing with ID 9999999 exists — npcExistsNearby(9999999) should be false");
    }

    // =========================================================================
    // objectExists predicate tests (renamed from npcExistsNearby)
    // =========================================================================

    [Fact]
    public async Task ObjectExists_NpcExists_ReturnsTrue()
    {
        var gameState = new FakeGameStateProvider();
        gameState.AddNpc(new NpcReference(new NpcId(1000789), new WorldPosition(10f, 0f, 10f), DistanceToPlayer: 5f));
        var evaluator = CreateEvaluator(gameState, new FakeQuestState());
        var ast = PredicateParser.Parse("objectExists(1000789)").Ast!;

        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task ObjectExists_NeitherExists_ReturnsFalse()
    {
        var gameState = new FakeGameStateProvider();
        var evaluator = CreateEvaluator(gameState, new FakeQuestState());
        var ast = PredicateParser.Parse("objectExists(9999999)").Ast!;

        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        Assert.False(result);
    }

    // =========================================================================
    // objectExistsInRange predicate tests
    // =========================================================================

    [Fact]
    public async Task ObjectExistsInRange_NpcWithinRange_ReturnsTrue()
    {
        var gameState = new FakeGameStateProvider();
        gameState.AddNpc(new NpcReference(new NpcId(1000789), new WorldPosition(10f, 0f, 10f), DistanceToPlayer: 5f));
        var evaluator = CreateEvaluator(gameState, new FakeQuestState());
        var ast = PredicateParser.Parse("objectExistsInRange(1000789, 10)").Ast!;

        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task ObjectExistsInRange_NpcOutOfRange_ReturnsFalse()
    {
        var gameState = new FakeGameStateProvider();
        gameState.AddNpc(new NpcReference(new NpcId(1000789), new WorldPosition(10f, 0f, 10f), DistanceToPlayer: 50f));
        var evaluator = CreateEvaluator(gameState, new FakeQuestState());
        var ast = PredicateParser.Parse("objectExistsInRange(1000789, 10)").Ast!;

        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task ObjectExistsInRange_InteractableWithinRange_ReturnsTrue()
    {
        var gameState = new FakeGameStateProvider();
        gameState.AddInteractable(new InteractableReference(
            new InteractableId(2001234), new WorldPosition(5f, 0f, 5f), DistanceToPlayer: 3f, Kind: InteractableKind.EventTrigger));
        var evaluator = CreateEvaluator(gameState, new FakeQuestState());
        var ast = PredicateParser.Parse("objectExistsInRange(2001234, 5)").Ast!;

        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task ObjectExistsInRange_NeitherExists_ReturnsFalse()
    {
        var gameState = new FakeGameStateProvider();
        var evaluator = CreateEvaluator(gameState, new FakeQuestState());
        var ast = PredicateParser.Parse("objectExistsInRange(9999999, 100)").Ast!;

        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task ObjectExistsInRange_ExactlyAtRange_ReturnsTrue()
    {
        var gameState = new FakeGameStateProvider();
        gameState.AddNpc(new NpcReference(new NpcId(100), new WorldPosition(0f, 0f, 0f), DistanceToPlayer: 10f));
        var evaluator = CreateEvaluator(gameState, new FakeQuestState());
        var ast = PredicateParser.Parse("objectExistsInRange(100, 10)").Ast!;

        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        Assert.True(result);
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