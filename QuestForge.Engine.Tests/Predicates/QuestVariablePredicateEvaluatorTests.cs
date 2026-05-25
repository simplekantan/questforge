using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Predicates;
using QuestForge.Predicates;
using Xunit;

namespace QuestForge.Engine.Tests.Predicates;

/// <summary>
/// Engine-evaluation tests for the questVariable predicate (EV1-EV7).
///
/// RED PHASE: All tests fail because PredicateEvaluator has no "questVariable" arm.
/// Evaluating "questVariable(…)" hits the default throw in the switch, which
/// throws UnknownStateFunctionException — NOT the expected successful evaluation.
///
/// Source of truth: docs/QUEST_VARIABLE_PREDICATE_PLAN.md §Given-When-Then §Engine evaluation
/// </summary>
public sealed class QuestVariablePredicateEvaluatorTests
{
    private static readonly QuestId ActiveQuest = new(66104);

    // =========================================================================
    // EV1 — happy: reads the indexed byte at index 0
    // =========================================================================

    [Fact]
    public async Task QuestVariable_Index0_ReturnsCorrectByte()
    {
        /*
         * RED: EvaluateFunction hits the default arm and throws UnknownStateFunctionException.
         *
         * CONTRACT (EV1 — part 1): Given SetQuestVariables(66104, 3,0,0,0,0,0),
         *                           When evaluating "questVariable(66104, 0) >= 3",
         *                           Then result is true.
         *
         * BUILDER GUIDANCE: Add "questVariable" arm to EvaluateFunction that calls
         *   _questState.GetQuestVariables(new QuestId((uint)(long)args[0]), ct).ValueOrThrow,
         *   then returns (long)vars[(int)(long)args[1]] (with runtime bounds guard).
         */

        // Arrange
        var questState = new FakeQuestState();
        questState.SetQuestVariables(ActiveQuest, 3, 0, 0, 0, 0, 0);
        var evaluator = CreateEvaluator(new FakeGameStateProvider(), questState);
        var ast = PredicateParser.Parse("questVariable(66104, 0) >= 3").Ast!;

        // Act
        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        // Assert
        Assert.True(result, "V0=3 should satisfy >= 3");
    }

    [Fact]
    public async Task QuestVariable_Index0_AboveValue_ReturnsFalse()
    {
        /*
         * RED: Same as above — hits unknown-function throw.
         *
         * CONTRACT (EV1 — part 2): Given SetQuestVariables(66104, 3,0,0,0,0,0),
         *                           When evaluating "questVariable(66104, 0) >= 4",
         *                           Then result is false.
         */

        // Arrange
        var questState = new FakeQuestState();
        questState.SetQuestVariables(ActiveQuest, 3, 0, 0, 0, 0, 0);
        var evaluator = CreateEvaluator(new FakeGameStateProvider(), questState);
        var ast = PredicateParser.Parse("questVariable(66104, 0) >= 4").Ast!;

        // Act
        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        // Assert
        Assert.False(result, "V0=3 should NOT satisfy >= 4");
    }

    // =========================================================================
    // EV2 — non-zero index is read (not always index 0)
    // =========================================================================

    [Fact]
    public async Task QuestVariable_Index3_ReturnsCorrectByte()
    {
        /*
         * RED: Throws UnknownStateFunctionException.
         *
         * CONTRACT (EV2 — part 1): Given SetQuestVariables(66104, 0,0,0,7,0,0),
         *                           When evaluating "questVariable(66104, 3) == 7",
         *                           Then result is true.
         *                           (Proves index 3 is read, not always index 0.)
         */

        // Arrange
        var questState = new FakeQuestState();
        questState.SetQuestVariables(ActiveQuest, 0, 0, 0, 7, 0, 0);
        var evaluator = CreateEvaluator(new FakeGameStateProvider(), questState);
        var ast = PredicateParser.Parse("questVariable(66104, 3) == 7").Ast!;

        // Act
        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        // Assert
        Assert.True(result, "V3=7 should satisfy == 7");
    }

    [Fact]
    public async Task QuestVariable_Index3_WrongValue_ReturnsFalse()
    {
        /*
         * RED: Throws UnknownStateFunctionException.
         *
         * CONTRACT (EV2 — part 2): Given SetQuestVariables(66104, 0,0,0,7,0,0),
         *                           When evaluating "questVariable(66104, 3) == 0",
         *                           Then result is false.
         */

        // Arrange
        var questState = new FakeQuestState();
        questState.SetQuestVariables(ActiveQuest, 0, 0, 0, 7, 0, 0);
        var evaluator = CreateEvaluator(new FakeGameStateProvider(), questState);
        var ast = PredicateParser.Parse("questVariable(66104, 3) == 0").Ast!;

        // Act
        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        // Assert
        Assert.False(result, "V3=7 should NOT satisfy == 0");
    }

    // =========================================================================
    // EV3 — not-accepted quest → all-zero → 0 (D5 semantics)
    // =========================================================================

    [Fact]
    public async Task QuestVariable_NotAcceptedQuest_ReturnsZero()
    {
        /*
         * RED: Throws UnknownStateFunctionException.
         *
         * CONTRACT (EV3 — part 1): Given no SetQuestVariables call for quest 66104
         *                           (FakeQuestState returns new byte[6] i.e. all zeros),
         *                           When evaluating "questVariable(66104, 0) == 0",
         *                           Then result is true.
         *
         * BUILDER GUIDANCE: IQuestState.GetQuestVariables returns a 6-zero list for
         *   a quest that has never had SetQuestVariables called — the adapter contract
         *   (D5) gives not-accepted-quest-equals-0 for free with no special-casing.
         */

        // Arrange — no SetQuestVariables call
        var questState = new FakeQuestState();
        var evaluator = CreateEvaluator(new FakeGameStateProvider(), questState);
        var ast = PredicateParser.Parse("questVariable(66104, 0) == 0").Ast!;

        // Act
        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        // Assert
        Assert.True(result, "Not-accepted quest: V0 should be 0, satisfying == 0");
    }

    [Fact]
    public async Task QuestVariable_NotAcceptedQuest_GreaterThanZero_ReturnsFalse()
    {
        /*
         * RED: Throws UnknownStateFunctionException.
         *
         * CONTRACT (EV3 — part 2): Given no SetQuestVariables call (all zeros),
         *                           When evaluating "questVariable(66104, 0) >= 1",
         *                           Then result is false.
         */

        // Arrange — no SetQuestVariables call
        var questState = new FakeQuestState();
        var evaluator = CreateEvaluator(new FakeGameStateProvider(), questState);
        var ast = PredicateParser.Parse("questVariable(66104, 0) >= 1").Ast!;

        // Act
        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        // Assert
        Assert.False(result, "Not-accepted quest: V0 is 0, should NOT satisfy >= 1");
    }

    // =========================================================================
    // EV4 — return type composes as Int across all comparison operators
    // =========================================================================

    [Theory]
    [InlineData("questVariable(66104, 0) >= 5", true)]
    [InlineData("questVariable(66104, 0) > 5",  false)]
    [InlineData("questVariable(66104, 0) <= 5", true)]
    [InlineData("questVariable(66104, 0) == 5", true)]
    [InlineData("questVariable(66104, 0) != 5", false)]
    public async Task QuestVariable_ComposesAsIntWithAllComparisonOperators(string predicate, bool expected)
    {
        /*
         * RED: Throws UnknownStateFunctionException.
         *
         * CONTRACT (EV4): Given SetQuestVariables(66104, 5,0,0,0,0,0),
         *                 Then each comparison operator produces the stated bool.
         *                 Proves the byte is widened to long and hits the Int comparison path.
         *
         * BUILDER GUIDANCE: Return (long)vars[(int)index] — the comparison path in
         *   EvaluateComparison handles long vs long correctly for all six operators.
         */

        // Arrange
        var questState = new FakeQuestState();
        questState.SetQuestVariables(ActiveQuest, 5, 0, 0, 0, 0, 0);
        var evaluator = CreateEvaluator(new FakeGameStateProvider(), questState);
        var ast = PredicateParser.Parse(predicate).Ast!;

        // Act
        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        // Assert
        Assert.Equal(expected, result);
    }

    // =========================================================================
    // EV5 — reuses the active-quest read; no GetQuestVariables read for any other quest id
    // =========================================================================

    [Fact]
    public async Task QuestVariable_OnlyReadsVariablesForTheSpecifiedQuestId()
    {
        /*
         * RED: Throws UnknownStateFunctionException.
         *
         * CONTRACT (EV5): When evaluating questVariable(66104, 0) >= 1,
         *                 ALL GetQuestVariables reads in SpyQuestState.VariableReads
         *                 must carry QuestId(66104) only — no other quest id is read.
         *                 (Pins D6 case-1: the evaluator must not fan out to other quests.)
         *
         * BUILDER GUIDANCE: The switch arm calls GetQuestVariables with the questId
         *   arg from the predicate (cast directly to QuestId). No pre-fetch or fan-out.
         */

        // Arrange
        var spy = new SpyQuestState();
        spy.SetQuestVariables(ActiveQuest, 1, 0, 0, 0, 0, 0);
        var evaluator = CreateEvaluatorWithSpy(new FakeGameStateProvider(), spy);
        var ast = PredicateParser.Parse("questVariable(66104, 0) >= 1").Ast!;

        // Act
        await evaluator.Evaluate(ast, CancellationToken.None);

        // Assert — every GetQuestVariables read must be for quest 66104 only
        var variableReads = spy.VariableReads;
        Assert.NotEmpty(variableReads);
        Assert.All(variableReads, questId =>
            Assert.Equal(ActiveQuest, questId));
    }

    // =========================================================================
    // EV6 — adapter failure propagates; does NOT silently return 0
    // =========================================================================

    [Fact]
    public async Task QuestVariable_AdapterFailure_Throws()
    {
        /*
         * RED: Throws UnknownStateFunctionException (wrong exception type).
         *
         * CONTRACT (EV6): Given an IQuestState whose GetQuestVariables returns Result.Fail,
         *                 When evaluating "questVariable(66104, 0) >= 1",
         *                 Then evaluation throws InvalidOperationException
         *                 (the same exception .ValueOrThrow raises on a Failure result).
         *                 It does NOT silently evaluate to false/0.
         *
         * BUILDER GUIDANCE: Use .ValueOrThrow exactly like the questSequence arm. A
         *   Result.Fail throws InvalidOperationException in .ValueOrThrow (see ResultTests.cs).
         *   Rationale (D5): silently returning 0 on a read failure would let a kill-count
         *   gate (questVariable(q,0) >= 3) evaluate false and stall the run forever.
         */

        // Arrange — use a failing fake that returns Result.Fail for GetQuestVariables
        var failing = new FailingQuestState();
        var evaluator = CreateEvaluatorWithSpy(new FakeGameStateProvider(), failing);
        var ast = PredicateParser.Parse("questVariable(66104, 0) >= 1").Ast!;

        // Act & Assert — must throw, not return false
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => evaluator.Evaluate(ast, CancellationToken.None));
    }

    // =========================================================================
    // EV7 — runtime out-of-bounds index → 0, no throw (defence-in-depth)
    // =========================================================================

    [Fact]
    public async Task QuestVariable_RuntimeOutOfBoundsIndex_ReturnsZeroWithoutThrowing()
    {
        /*
         * RED: Throws UnknownStateFunctionException.
         *
         * CONTRACT (EV7): Given SetQuestVariables(66104, 1,1,1,1,1,1) and a parsed
         *                 "questVariable(66104, 9) == 0" AST (index 9 is past the 6-byte
         *                 array; the checker would reject a literal 9, but the parser
         *                 accepts it per PA3 — no parser change was made),
         *                 When evaluated,
         *                 Then result is true AND no exception is thrown.
         *
         * BUILDER GUIDANCE: In EvaluateQuestVariable, after fetching vars, add:
         *   if (index < 0 || index >= vars.Count) return 0L;
         *   This guards parameterised indices and predicates that bypassed qf-validate.
         *   Choosing 0 (not throwing) keeps a mis-authored fragment from crashing a run.
         */

        // Arrange — parse with the parser (which accepts any int literal), bypassing the checker
        var questState = new FakeQuestState();
        questState.SetQuestVariables(ActiveQuest, 1, 1, 1, 1, 1, 1);
        var evaluator = CreateEvaluator(new FakeGameStateProvider(), questState);

        // The parser accepts index 9 (PA3 established this). Use the quest id as a raw number.
        var ast = PredicateParser.Parse("questVariable(66104, 9) == 0").Ast!;

        // Act — must not throw
        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        // Assert — out-of-bounds index returns 0 → 0 == 0 is true
        Assert.True(result,
            "Out-of-bounds index 9 should return 0 (not throw), making == 0 true");
    }

    // =========================================================================
    // Factory helpers — mirror PredicateEvaluatorTests.cs pattern
    // =========================================================================

    private static PredicateEvaluator CreateEvaluator(
        FakeGameStateProvider gameState,
        FakeQuestState questState)
        => new PredicateEvaluator(gameState, questState);

    private static PredicateEvaluator CreateEvaluatorWithSpy(
        FakeGameStateProvider gameState,
        IQuestState questState)
        => new PredicateEvaluator(gameState, questState);
}

// =========================================================================
// SpyQuestState — records the quest id passed to each GetQuestVariables call.
// Used by EV5 to verify the evaluator does not fan out to unexpected quest ids.
// Wraps FakeQuestState for all other methods.
// =========================================================================
file sealed class SpyQuestState : IQuestState
{
    private readonly FakeQuestState _inner = new();
    private readonly List<QuestId> _variableReads = new();

    /// <summary>Quest ids passed to every GetQuestVariables call, in order.</summary>
    public IReadOnlyList<QuestId> VariableReads => _variableReads;

    public void SetQuestVariables(QuestId quest, params byte[] variables)
        => _inner.SetQuestVariables(quest, variables);

    public Task<Result<IReadOnlyList<byte>>> GetQuestVariables(QuestId quest, CancellationToken ct)
    {
        _variableReads.Add(quest);
        return _inner.GetQuestVariables(quest, ct);
    }

    // All other IQuestState members delegate to FakeQuestState unchanged.
    public Task<Result<QuestStatus>> GetQuestStatus(QuestId quest, CancellationToken ct)
        => _inner.GetQuestStatus(quest, ct);
    public Task<Result<int>> GetQuestSequence(QuestId quest, CancellationToken ct)
        => _inner.GetQuestSequence(quest, ct);
    public Task<Result<uint>> GetQuestFlags(QuestId quest, CancellationToken ct)
        => _inner.GetQuestFlags(quest, ct);
    public Task<Result<bool>> IsQuestFlagSet(QuestId quest, int flagBit, CancellationToken ct)
        => _inner.IsQuestFlagSet(quest, flagBit, ct);
    public Task<Result<bool>> IsQuestAccepted(QuestId quest, CancellationToken ct)
        => _inner.IsQuestAccepted(quest, ct);
    public Task<Result<bool>> IsQuestComplete(QuestId quest, CancellationToken ct)
        => _inner.IsQuestComplete(quest, ct);
    public Task<Result<bool>> IsQuestAvailable(QuestId quest, CancellationToken ct)
        => _inner.IsQuestAvailable(quest, ct);
    public Task<Result<QuestUnlockReason?>> WhyUnavailable(QuestId quest, CancellationToken ct)
        => _inner.WhyUnavailable(quest, ct);
    public Task<Result<bool>> IsAcceptableNow(QuestId quest, CancellationToken ct)
        => _inner.IsAcceptableNow(quest, ct);
    public Task<Result<IReadOnlyList<QuestId>>> GetAcceptedQuests(CancellationToken ct)
        => _inner.GetAcceptedQuests(ct);
    public Task<Result<IReadOnlyList<QuestReward>>> GetAvailableQuestRewards(CancellationToken ct)
        => _inner.GetAvailableQuestRewards(ct);
}

// =========================================================================
// FailingQuestState — GetQuestVariables always returns Result.Fail.
// Used by EV6 to verify the evaluator propagates adapter failures (does not
// swallow them with a silent 0).
// =========================================================================
file sealed class FailingQuestState : IQuestState
{
    public Task<Result<IReadOnlyList<byte>>> GetQuestVariables(QuestId quest, CancellationToken ct)
        => Task.FromResult<Result<IReadOnlyList<byte>>>(
            Result.Fail<IReadOnlyList<byte>>("simulated-adapter-failure"));

    // All other members throw NotSupportedException — they are not needed by EV6.
    public Task<Result<QuestStatus>> GetQuestStatus(QuestId quest, CancellationToken ct)
        => throw new NotSupportedException();
    public Task<Result<int>> GetQuestSequence(QuestId quest, CancellationToken ct)
        => throw new NotSupportedException();
    public Task<Result<uint>> GetQuestFlags(QuestId quest, CancellationToken ct)
        => throw new NotSupportedException();
    public Task<Result<bool>> IsQuestFlagSet(QuestId quest, int flagBit, CancellationToken ct)
        => throw new NotSupportedException();
    public Task<Result<bool>> IsQuestAccepted(QuestId quest, CancellationToken ct)
        => throw new NotSupportedException();
    public Task<Result<bool>> IsQuestComplete(QuestId quest, CancellationToken ct)
        => throw new NotSupportedException();
    public Task<Result<bool>> IsQuestAvailable(QuestId quest, CancellationToken ct)
        => throw new NotSupportedException();
    public Task<Result<QuestUnlockReason?>> WhyUnavailable(QuestId quest, CancellationToken ct)
        => throw new NotSupportedException();
    public Task<Result<bool>> IsAcceptableNow(QuestId quest, CancellationToken ct)
        => throw new NotSupportedException();
    public Task<Result<IReadOnlyList<QuestId>>> GetAcceptedQuests(CancellationToken ct)
        => throw new NotSupportedException();
    public Task<Result<IReadOnlyList<QuestReward>>> GetAvailableQuestRewards(CancellationToken ct)
        => throw new NotSupportedException();
}
