using QuestForge.Adapters.State;
using QuestForge.Predicates;
using QuestForge.Schema;

namespace QuestForge.Engine.Predicates;

public sealed class PredicateEvaluator
{
    private readonly IGameStateProvider _gameState;
    private readonly IQuestState _questState;

    public PredicateEvaluator(IGameStateProvider gameState, IQuestState questState)
    {
        _gameState = gameState ?? throw new ArgumentNullException(nameof(gameState));
        _questState = questState ?? throw new ArgumentNullException(nameof(questState));
    }

    /// <summary>Evaluate a parsed AST against live state.</summary>
    public Task<bool> Evaluate(PredicateAst ast, CancellationToken ct) =>
        throw new NotImplementedException();
}

public sealed class ExpectEvaluator
{
    private readonly PredicateEvaluator _inner;

    public ExpectEvaluator(PredicateEvaluator inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <summary>Parse (with caching) and evaluate any ExpectValue form. Null returns true.</summary>
    public Task<bool> Evaluate(ExpectValue? expect, CancellationToken ct) =>
        throw new NotImplementedException();

    /// <summary>Parse and evaluate a raw predicate string. Null returns false (sequence not skipped).</summary>
    public Task<bool> Evaluate(string? predicate, CancellationToken ct) =>
        throw new NotImplementedException();
}