using QuestForge.Adapters.State;
using QuestForge.Adapters.Types;
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
    public async Task<bool> Evaluate(PredicateAst ast, CancellationToken ct)
    {
        var result = await EvaluateInternal(ast, ct);
        if (result is bool b)
            return b;
        throw new EnginePredicateShapeException(
            $"Predicate evaluated to {result?.GetType().Name ?? "null"} instead of bool at the top level. " +
            $"Predicate expressions must return bool.");
    }

    private async Task<object> EvaluateInternal(PredicateAst ast, CancellationToken ct)
    {
        switch (ast)
        {
            case PredicateAst.DefaultLiteral:
                return true;

            case PredicateAst.IntLiteral(var value):
                return value;

            case PredicateAst.StringLiteral(var value):
                return value;

            case PredicateAst.PositionLiteral(var x, var y, var z):
                return new WorldPosition(x, y, z);

            case PredicateAst.FunctionCall(var name, var args):
                return await EvaluateFunction(name, args, ct);

            case PredicateAst.Comparison(var left, var op, var right):
                return await EvaluateComparison(left, op, right, ct);

            case PredicateAst.And(var left, var right):
            {
                var leftResult = await EvaluateInternal(left, ct);
                if (leftResult is not bool leftBool)
                    throw new EnginePredicateShapeException($"Left operand of 'and' did not evaluate to bool.");
                if (!leftBool) return false;
                var rightResult = await EvaluateInternal(right, ct);
                if (rightResult is not bool rightBool)
                    throw new EnginePredicateShapeException($"Right operand of 'and' did not evaluate to bool.");
                return rightBool;
            }

            case PredicateAst.Or(var left, var right):
            {
                var leftResult = await EvaluateInternal(left, ct);
                if (leftResult is not bool leftBool)
                    throw new EnginePredicateShapeException($"Left operand of 'or' did not evaluate to bool.");
                if (leftBool) return true;
                var rightResult = await EvaluateInternal(right, ct);
                if (rightResult is not bool rightBool)
                    throw new EnginePredicateShapeException($"Right operand of 'or' did not evaluate to bool.");
                return rightBool;
            }

            case PredicateAst.Not(var inner):
            {
                var innerResult = await EvaluateInternal(inner, ct);
                if (innerResult is not bool innerBool)
                    throw new EnginePredicateShapeException($"Operand of 'not' did not evaluate to bool.");
                return !innerBool;
            }

            case PredicateAst.ParameterRef:
                throw new NotSupportedException(
                    "PredicateAst.ParameterRef requires a fragment parameter scope — not supported until Phase 7+");

            default:
                throw new NotSupportedException($"Unhandled PredicateAst node type: {ast.GetType().Name}");
        }
    }

    private async Task<object> EvaluateFunction(string name, IReadOnlyList<PredicateAst> argNodes, CancellationToken ct)
    {
        var args = new object[argNodes.Count];
        for (var i = 0; i < argNodes.Count; i++)
            args[i] = await EvaluateInternal(argNodes[i], ct);

        return name switch
        {
            "questSequence" => (long)(await _questState.GetQuestSequence(new QuestId((uint)(long)args[0]), ct)).ValueOrThrow,
            "isQuestAccepted" => (await _questState.IsQuestAccepted(new QuestId((uint)(long)args[0]), ct)).ValueOrThrow,
            "isQuestComplete" => (await _questState.IsQuestComplete(new QuestId((uint)(long)args[0]), ct)).ValueOrThrow,
            "questFlag" => (await _questState.IsQuestFlagSet(new QuestId((uint)(long)args[0]), (int)(long)args[1], ct)).ValueOrThrow,
            "playerZone" => (long)(await _gameState.GetPlayerZone(ct)).ValueOrThrow.Value,
            "playerNear" => await EvaluatePlayerNear((WorldPosition)args[0], (long)args[1], ct),
            "playerInCombat" => (await _gameState.IsPlayerInCombat(ct)).ValueOrThrow,
            "isAttuned" => (await _gameState.IsAetheryteAttuned(
                new QuestForge.Adapters.Types.AetheryteId((uint)(long)args[0]), ct)).ValueOrThrow,
            _ => throw new UnknownStateFunctionException(name)
        };
    }

    private async Task<object> EvaluatePlayerNear(WorldPosition target, long radius, CancellationToken ct)
    {
        var playerPos = (await _gameState.GetPlayerPosition(ct)).ValueOrThrow;
        var distance = playerPos.DistanceTo(target);
        return distance <= (float)radius;
    }

    private async Task<object> EvaluateComparison(PredicateAst left, ComparisonOp op, PredicateAst right, CancellationToken ct)
    {
        var leftVal = await EvaluateInternal(left, ct);
        var rightVal = await EvaluateInternal(right, ct);

        if (leftVal is long lLong && rightVal is long rLong)
        {
            return op switch
            {
                ComparisonOp.Eq => lLong == rLong,
                ComparisonOp.NotEq => lLong != rLong,
                ComparisonOp.Gt => lLong > rLong,
                ComparisonOp.Lt => lLong < rLong,
                ComparisonOp.GtEq => lLong >= rLong,
                ComparisonOp.LtEq => lLong <= rLong,
                _ => throw new EnginePredicateShapeException($"Unknown comparison operator: {op}")
            };
        }

        if (leftVal is string lStr && rightVal is string rStr)
        {
            return op switch
            {
                ComparisonOp.Eq => lStr == rStr,
                ComparisonOp.NotEq => lStr != rStr,
                _ => throw new EnginePredicateShapeException($"Operator {op} not supported for string comparison.")
            };
        }

        throw new EnginePredicateShapeException(
            $"Cannot compare {leftVal?.GetType().Name} with {rightVal?.GetType().Name}.");
    }
}

public sealed class ExpectEvaluator
{
    private readonly PredicateEvaluator _inner;
    private readonly Dictionary<string, PredicateAst> _cache = new();

    public ExpectEvaluator(PredicateEvaluator inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <summary>Parse (with caching) and evaluate any ExpectValue form. Null returns true.</summary>
    public async Task<bool> Evaluate(ExpectValue? expect, CancellationToken ct)
    {
        if (expect is null)
            return true;

        switch (expect)
        {
            case PredicateExpect predicateExpect:
                return await EvaluatePredicate(predicateExpect.Predicate, ct);

            case AllExpect allExpect:
                foreach (var predicate in allExpect.All)
                    if (!await EvaluatePredicate(predicate, ct))
                        return false;
                return true;

            case AnyExpect anyExpect:
                foreach (var predicate in anyExpect.Any)
                    if (await EvaluatePredicate(predicate, ct))
                        return true;
                return false;

            default:
                throw new EnginePredicateShapeException($"Unknown ExpectValue subtype: {expect.GetType().Name}");
        }
    }

    /// <summary>Parse and evaluate a raw predicate string. Null returns false (sequence not skipped).</summary>
    public async Task<bool> Evaluate(string? predicate, CancellationToken ct)
    {
        if (predicate is null)
            return false;

        return await EvaluatePredicate(predicate, ct);
    }

    private async Task<bool> EvaluatePredicate(string predicateStr, CancellationToken ct)
    {
        if (!_cache.TryGetValue(predicateStr, out var ast))
        {
            var parseResult = PredicateParser.Parse(predicateStr);
            if (!parseResult.IsSuccess || parseResult.Ast is null)
                throw new EnginePredicateShapeException(
                    $"Failed to parse predicate '{predicateStr}': {string.Join("; ", parseResult.Errors.Select(e => e.Message))}");
            ast = parseResult.Ast;
            _cache[predicateStr] = ast;
        }

        return await _inner.Evaluate(ast, ct);
    }
}