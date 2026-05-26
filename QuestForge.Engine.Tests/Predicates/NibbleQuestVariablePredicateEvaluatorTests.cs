using QuestForge.Adapters.Fakes.State;
using QuestForge.Adapters.State;
using QuestForge.Adapters.Types;
using QuestForge.Engine.Predicates;
using QuestForge.Predicates;
using Xunit;

namespace QuestForge.Engine.Tests.Predicates;

/// <summary>
/// Engine-evaluation tests for the questVariableLow and questVariableHigh predicates (EV-N1 … EV-N11).
///
/// RED PHASE: Every test fails because PredicateEvaluator has no "questVariableLow" or
/// "questVariableHigh" arm in EvaluateFunction. The default arm throws
/// UnknownStateFunctionException for both names.
///
/// Source of truth: docs/NIBBLE_PREDICATES_PLAN.md §Given-When-Then §Engine evaluation (EV-N* series)
/// </summary>
public sealed class NibbleQuestVariablePredicateEvaluatorTests
{
    // The canonical quest id used by the COMBAT plan completion-gate examples.
    private static readonly QuestId CombatQuest = new(65847);

    private static string FormatErrors(Exception ex) => ex.Message;

    // =========================================================================
    // EV-N1 — low nibble extraction from 0x13 (low=3, high=1)
    // =========================================================================

    [Fact]
    public async Task NibbleLow_0x13_ReturnsLowNibble3()
    {
        /*
         * RED: EvaluateFunction hits the default arm and throws UnknownStateFunctionException.
         *
         * CONTRACT (EV-N1 — part 1): Given SetQuestVariables(65847, 0x13,0,0,0,0,0) (V0=0x13),
         *                             When evaluating "questVariableLow(65847, 0) == 3",
         *                             Then result is true. (0x13 & 0x0F == 3)
         */

        var questState = new FakeQuestState();
        questState.SetQuestVariables(CombatQuest, 0x13, 0, 0, 0, 0, 0);
        var evaluator = new PredicateEvaluator(new FakeGameStateProvider(), questState);
        var ast = PredicateParser.Parse("questVariableLow(65847, 0) == 3").Ast!;

        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        Assert.True(result, "0x13 & 0x0F == 3 should be true");
    }

    [Fact]
    public async Task NibbleLow_0x13_WholeByte_ReturnsFalse()
    {
        /*
         * RED: Throws UnknownStateFunctionException.
         *
         * CONTRACT (EV-N1 — part 2): Given V0=0x13 (decimal 19),
         *                             When evaluating "questVariableLow(65847, 0) == 19",
         *                             Then result is false.
         *                             (Proves the whole byte is NOT returned — only low nibble.)
         */

        var questState = new FakeQuestState();
        questState.SetQuestVariables(CombatQuest, 0x13, 0, 0, 0, 0, 0);
        var evaluator = new PredicateEvaluator(new FakeGameStateProvider(), questState);
        var ast = PredicateParser.Parse("questVariableLow(65847, 0) == 19").Ast!;

        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        Assert.False(result, "Low nibble of 0x13 is 3, not 19 — the whole byte must NOT be returned");
    }

    // =========================================================================
    // EV-N2 — high nibble extraction from 0x13 (low=3, high=1)
    // =========================================================================

    [Fact]
    public async Task NibbleHigh_0x13_ReturnsHighNibble1()
    {
        /*
         * RED: Throws UnknownStateFunctionException.
         *
         * CONTRACT (EV-N2 — part 1): Given V0=0x13,
         *                             When evaluating "questVariableHigh(65847, 0) == 1",
         *                             Then result is true. (0x13 >> 4 == 1)
         */

        var questState = new FakeQuestState();
        questState.SetQuestVariables(CombatQuest, 0x13, 0, 0, 0, 0, 0);
        var evaluator = new PredicateEvaluator(new FakeGameStateProvider(), questState);
        var ast = PredicateParser.Parse("questVariableHigh(65847, 0) == 1").Ast!;

        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        Assert.True(result, "0x13 >> 4 == 1 should be true");
    }

    [Fact]
    public async Task NibbleHigh_0x13_LowNibbleValue_ReturnsFalse()
    {
        /*
         * RED: Throws UnknownStateFunctionException.
         *
         * CONTRACT (EV-N2 — part 2): Given V0=0x13,
         *                             When evaluating "questVariableHigh(65847, 0) == 3",
         *                             Then result is false.
         *                             (Proves High != Low — high is 1, not 3.)
         */

        var questState = new FakeQuestState();
        questState.SetQuestVariables(CombatQuest, 0x13, 0, 0, 0, 0, 0);
        var evaluator = new PredicateEvaluator(new FakeGameStateProvider(), questState);
        var ast = PredicateParser.Parse("questVariableHigh(65847, 0) == 3").Ast!;

        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        Assert.False(result, "High nibble of 0x13 is 1, not 3");
    }

    // =========================================================================
    // EV-N3 — nibble of 0x00: both halves are zero
    // =========================================================================

    [Theory]
    [InlineData("questVariableLow(65847, 0) == 0", true)]
    [InlineData("questVariableHigh(65847, 0) == 0", true)]
    public async Task Nibble_ZeroByte_BothHalvesZero(string predicate, bool expected)
    {
        /*
         * RED: Throws UnknownStateFunctionException.
         *
         * CONTRACT (EV-N3): Given V0=0x00, both Low and High return 0.
         *                   (Also covers the not-accepted path: FakeQuestState returns
         *                    all-zeros for a quest with no SetQuestVariables call.)
         */

        var questState = new FakeQuestState();
        questState.SetQuestVariables(CombatQuest, 0x00, 0, 0, 0, 0, 0);
        var evaluator = new PredicateEvaluator(new FakeGameStateProvider(), questState);
        var ast = PredicateParser.Parse(predicate).Ast!;

        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        Assert.Equal(expected, result);
    }

    // =========================================================================
    // EV-N4 — nibble of 0xFF: both halves are 15
    // =========================================================================

    [Theory]
    [InlineData("questVariableLow(65847, 0) == 15", true)]
    [InlineData("questVariableHigh(65847, 0) == 15", true)]
    public async Task Nibble_0xFF_BothHalves15(string predicate, bool expected)
    {
        /*
         * RED: Throws UnknownStateFunctionException.
         *
         * CONTRACT (EV-N4): Given V0=0xFF,
         *                   Low == 0xFF & 0x0F == 15, High == 0xFF >> 4 == 15.
         *                   Pins upper bound and proves High uses >> 4 (not & 0xF0).
         */

        var questState = new FakeQuestState();
        questState.SetQuestVariables(CombatQuest, 0xFF, 0, 0, 0, 0, 0);
        var evaluator = new PredicateEvaluator(new FakeGameStateProvider(), questState);
        var ast = PredicateParser.Parse(predicate).Ast!;

        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        Assert.Equal(expected, result);
    }

    // =========================================================================
    // EV-N5 — verified quest-65847 multi-objective layout
    //         V0=0x03 (low=3), V1=0x33 (low=3, high=3)
    // =========================================================================

    [Theory]
    [InlineData("questVariableLow(65847, 0) >= 3", 0x03, 0x33, true)]
    [InlineData("questVariableLow(65847, 1) >= 3", 0x03, 0x33, true)]
    [InlineData("questVariableHigh(65847, 1) >= 3", 0x03, 0x33, true)]
    [InlineData("questVariableLow(65847, 0) >= 3", 0x02, 0x33, false)]  // V0 low=2, not yet complete
    public async Task CombatLayout_AllThreeObjectiveGates(string predicate, byte v0, byte v1, bool expected)
    {
        /*
         * RED: Throws UnknownStateFunctionException.
         *
         * CONTRACT (EV-N5): V0=0x03 (low=3), V1=0x33 (low=3, high=3) — the three
         *                   "defeat 3×" objectives at completion. With V0=0x02 the
         *                   first objective gate fails (low nibble 2 < 3).
         *                   Directly encodes the COMBAT plan completion gate.
         */

        var questState = new FakeQuestState();
        questState.SetQuestVariables(CombatQuest, v0, v1, 0, 0, 0, 0);
        var evaluator = new PredicateEvaluator(new FakeGameStateProvider(), questState);
        var ast = PredicateParser.Parse(predicate).Ast!;

        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        Assert.Equal(expected, result);
    }

    // =========================================================================
    // EV-N6 — non-zero index: V3=0x5A (low=10, high=5)
    // =========================================================================

    [Theory]
    [InlineData("questVariableLow(65847, 3) == 10", true)]
    [InlineData("questVariableHigh(65847, 3) == 5", true)]
    public async Task Nibble_NonZeroIndex_ReadsCorrectByte(string predicate, bool expected)
    {
        /*
         * RED: Throws UnknownStateFunctionException.
         *
         * CONTRACT (EV-N6): Given V3=0x5A, Low(i=3)==10, High(i=3)==5.
         *                   Proves index 3 is read, not index 0. (0x5A: low=0xA=10, high=5.)
         */

        var questState = new FakeQuestState();
        questState.SetQuestVariables(CombatQuest, 0, 0, 0, 0x5A, 0, 0);
        var evaluator = new PredicateEvaluator(new FakeGameStateProvider(), questState);
        var ast = PredicateParser.Parse(predicate).Ast!;

        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        Assert.Equal(expected, result);
    }

    // =========================================================================
    // EV-N7 — composes as Int across all comparison operators (V0=0x35, low=5)
    // =========================================================================

    [Theory]
    [InlineData("questVariableLow(65847, 0) >= 5", true)]
    [InlineData("questVariableLow(65847, 0) > 5",  false)]
    [InlineData("questVariableLow(65847, 0) <= 5", true)]
    [InlineData("questVariableLow(65847, 0) == 5", true)]
    [InlineData("questVariableLow(65847, 0) != 5", false)]
    public async Task NibbleLow_ComposesAsIntWithAllComparisonOperators(string predicate, bool expected)
    {
        /*
         * RED: Throws UnknownStateFunctionException.
         *
         * CONTRACT (EV-N7): Given V0=0x35 (low nibble=5), the nibble is boxed as long
         *                   and composes with all six Int comparison operators.
         */

        var questState = new FakeQuestState();
        questState.SetQuestVariables(CombatQuest, 0x35, 0, 0, 0, 0, 0);
        var evaluator = new PredicateEvaluator(new FakeGameStateProvider(), questState);
        var ast = PredicateParser.Parse(predicate).Ast!;

        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        Assert.Equal(expected, result);
    }

    // =========================================================================
    // EV-N8 — not-accepted quest → adapter returns all-zeros → both nibbles 0
    // =========================================================================

    [Theory]
    [InlineData("questVariableLow(65847, 0) == 0", true)]
    [InlineData("questVariableHigh(65847, 0) == 0", true)]
    [InlineData("questVariableLow(65847, 0) >= 1", false)]
    public async Task Nibble_NotAcceptedQuest_ReturnsZero(string predicate, bool expected)
    {
        /*
         * RED: Throws UnknownStateFunctionException.
         *
         * CONTRACT (EV-N8): Given no SetQuestVariables call for quest 65847
         *                   (FakeQuestState returns new byte[6] all-zeros),
         *                   both Low and High evaluate to 0 for any index.
         */

        // Arrange — intentionally no SetQuestVariables call
        var questState = new FakeQuestState();
        var evaluator = new PredicateEvaluator(new FakeGameStateProvider(), questState);
        var ast = PredicateParser.Parse(predicate).Ast!;

        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        Assert.Equal(expected, result);
    }

    // =========================================================================
    // EV-N9 — only reads the specified quest id (no fan-out)
    // =========================================================================

    [Theory]
    [InlineData("questVariableLow(65847, 0) >= 1")]
    [InlineData("questVariableHigh(65847, 0) >= 1")]
    public async Task Nibble_OnlyReadsVariablesForSpecifiedQuestId(string predicate)
    {
        /*
         * RED: Throws UnknownStateFunctionException.
         *
         * CONTRACT (EV-N9): When evaluating a nibble predicate for quest 65847,
         *                   every GetQuestVariables call in SpyQuestState.VariableReads
         *                   must carry QuestId(65847) only — no fan-out to other quest ids.
         *                   Pins D7: same (method, arg) key as questVariable, no new read pattern.
         */

        var spy = new NibbleSpyQuestState();
        spy.SetQuestVariables(CombatQuest, 0x13, 0, 0, 0, 0, 0);
        var evaluator = new PredicateEvaluator(new FakeGameStateProvider(), spy);
        var ast = PredicateParser.Parse(predicate).Ast!;

        await evaluator.Evaluate(ast, CancellationToken.None);

        Assert.NotEmpty(spy.VariableReads);
        Assert.All(spy.VariableReads, questId =>
            Assert.Equal(CombatQuest, questId));
    }

    // =========================================================================
    // EV-N10 — adapter failure propagates; does NOT silently return 0
    // =========================================================================

    [Fact]
    public async Task NibbleLow_AdapterFailure_Throws()
    {
        /*
         * RED: Throws UnknownStateFunctionException (wrong exception type for this contract).
         *
         * CONTRACT (EV-N10 Low): Given GetQuestVariables returns Result.Fail,
         *                         When evaluating "questVariableLow(65847, 0) >= 1",
         *                         Then evaluation throws InvalidOperationException.
         *                         (ValueOrThrow propagates failure — does NOT return 0.)
         */

        var failing = new NibbleFailingQuestState();
        var evaluator = new PredicateEvaluator(new FakeGameStateProvider(), failing);
        var ast = PredicateParser.Parse("questVariableLow(65847, 0) >= 1").Ast!;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => evaluator.Evaluate(ast, CancellationToken.None));
    }

    [Fact]
    public async Task NibbleHigh_AdapterFailure_Throws()
    {
        /*
         * RED: Throws UnknownStateFunctionException (wrong exception type for this contract).
         *
         * CONTRACT (EV-N10 High): Symmetric to Low — adapter failure must propagate.
         */

        var failing = new NibbleFailingQuestState();
        var evaluator = new PredicateEvaluator(new FakeGameStateProvider(), failing);
        var ast = PredicateParser.Parse("questVariableHigh(65847, 0) >= 1").Ast!;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => evaluator.Evaluate(ast, CancellationToken.None));
    }

    // =========================================================================
    // EV-N11 — runtime out-of-bounds index → 0, no throw (defence-in-depth)
    // =========================================================================

    [Fact]
    public async Task NibbleLow_RuntimeOutOfBoundsIndex_ReturnsZeroWithoutThrowing()
    {
        /*
         * RED: Throws UnknownStateFunctionException.
         *
         * CONTRACT (EV-N11 Low): Given SetQuestVariables(65847, 1,1,1,1,1,1) and index 9,
         *                         When evaluated (parser accepts index 9 per PA-N3),
         *                         Then result is true (0 == 0) and no exception.
         *                         (The engine's bounds guard: if index >= vars.Count → return 0L.)
         */

        var questState = new FakeQuestState();
        questState.SetQuestVariables(CombatQuest, 1, 1, 1, 1, 1, 1);
        var evaluator = new PredicateEvaluator(new FakeGameStateProvider(), questState);
        // Parser accepts index 9 — the checker would reject it, but the engine must guard.
        var ast = PredicateParser.Parse("questVariableLow(65847, 9) == 0").Ast!;

        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        Assert.True(result, "Out-of-bounds index 9 should return 0 (not throw), making == 0 true");
    }

    [Fact]
    public async Task NibbleHigh_RuntimeOutOfBoundsIndex_ReturnsZeroWithoutThrowing()
    {
        /*
         * RED: Throws UnknownStateFunctionException.
         *
         * CONTRACT (EV-N11 High): Symmetric to Low.
         */

        var questState = new FakeQuestState();
        questState.SetQuestVariables(CombatQuest, 1, 1, 1, 1, 1, 1);
        var evaluator = new PredicateEvaluator(new FakeGameStateProvider(), questState);
        var ast = PredicateParser.Parse("questVariableHigh(65847, 9) == 0").Ast!;

        var result = await evaluator.Evaluate(ast, CancellationToken.None);

        Assert.True(result, "Out-of-bounds index 9 (High) should return 0 (not throw), making == 0 true");
    }
}

// =============================================================================
// NibbleSpyQuestState — records the quest id passed to each GetQuestVariables call.
// Used by EV-N9 to verify the evaluator does not fan out to unexpected quest ids.
// Wraps FakeQuestState for all other methods.
// =============================================================================
file sealed class NibbleSpyQuestState : IQuestState
{
    private readonly FakeQuestState _inner = new();
    private readonly List<QuestId> _variableReads = new();

    public IReadOnlyList<QuestId> VariableReads => _variableReads;

    public void SetQuestVariables(QuestId quest, params byte[] variables)
        => _inner.SetQuestVariables(quest, variables);

    public Task<Result<IReadOnlyList<byte>>> GetQuestVariables(QuestId quest, CancellationToken ct)
    {
        _variableReads.Add(quest);
        return _inner.GetQuestVariables(quest, ct);
    }

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

// =============================================================================
// NibbleFailingQuestState — GetQuestVariables always returns Result.Fail.
// Used by EV-N10 to verify the evaluator propagates adapter failures (does not
// swallow them with a silent 0).
// =============================================================================
file sealed class NibbleFailingQuestState : IQuestState
{
    public Task<Result<IReadOnlyList<byte>>> GetQuestVariables(QuestId quest, CancellationToken ct)
        => Task.FromResult<Result<IReadOnlyList<byte>>>(
            Result.Fail<IReadOnlyList<byte>>("simulated-nibble-adapter-failure"));

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
