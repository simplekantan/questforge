using QuestForge.Adapters.Types;
using QuestForge.Engine.Predicates;
using QuestForge.Predicates;
using QuestForge.Schema;

namespace QuestForge.Engine.Tests.Replay;

internal abstract record StateMutation
{
    internal sealed record SetQuestSequence(QuestId Quest, int Value) : StateMutation;
    internal sealed record SetQuestComplete(QuestId Quest) : StateMutation;
    internal sealed record SetQuestAccepted(QuestId Quest) : StateMutation;
    internal sealed record SetPlayerZone(int Zone) : StateMutation;
    internal sealed record SetPlayerPosition(float X, float Y, float Z) : StateMutation;
    internal sealed record SetPlayerNear(float X, float Y, float Z, float Radius) : StateMutation;
    internal sealed record SetQuestFlag(QuestId Quest, int Bit, bool Value) : StateMutation;
    internal sealed record SetQuestVariable(QuestId Quest, int Index, int Value, Nibble Nibble) : StateMutation;
    internal sealed record SetAttuned(uint AetheryteId) : StateMutation;
    internal sealed record SetPlayerHasItem(uint ItemId, int Quantity) : StateMutation;
    internal sealed record SetNotPredicate(StateMutation Inner) : StateMutation;
    internal sealed record SetObjectExistsInRange(uint DataId, float Range) : StateMutation;
    internal sealed record SetNpcExistsNearby(uint DataId) : StateMutation;
    internal sealed record SetSlotEquipped(int SlotIndex, int ItemLevel) : StateMutation;
    internal sealed record SetItemEquipped(uint ItemId) : StateMutation;
    internal sealed record SetInCombat(bool Value) : StateMutation;
    internal sealed record SetGearsetExistsForJob(uint JobId) : StateMutation;
    internal sealed record SetHasCoffers(bool Value) : StateMutation;
    internal sealed record SetAetherCurrentAttuned(uint Id) : StateMutation;
}

internal static class PredicateAnalyzer
{
    public static IReadOnlyList<StateMutation> ExtractMutations(ExpectValue? expect, QuestId activeQuestId)
    {
        if (expect is null)
            return Array.Empty<StateMutation>();

        switch (expect)
        {
            case PredicateExpect predicateExpect:
                return ExtractFromString(predicateExpect.Predicate, activeQuestId);

            case AllExpect allExpect:
            {
                var result = new List<StateMutation>();
                foreach (var predicate in allExpect.All)
                    result.AddRange(ExtractFromString(predicate, activeQuestId));
                return result;
            }

            case AnyExpect anyExpect:
                if (anyExpect.Any.Length == 0)
                    return Array.Empty<StateMutation>();
                return ExtractFromString(anyExpect.Any[0], activeQuestId);

            default:
                return Array.Empty<StateMutation>();
        }
    }

    private static IReadOnlyList<StateMutation> ExtractFromString(string predicateStr, QuestId activeQuestId)
    {
        var parseResult = PredicateParser.Parse(predicateStr);
        if (!parseResult.IsSuccess || parseResult.Ast is null)
            return Array.Empty<StateMutation>();

        var mutations = new List<StateMutation>();
        WalkAst(parseResult.Ast, mutations);
        return mutations;
    }

    private static void WalkAst(PredicateAst ast, List<StateMutation> mutations)
    {
        switch (ast)
        {
            case PredicateAst.And(var left, var right):
                WalkAst(left, mutations);
                WalkAst(right, mutations);
                break;

            case PredicateAst.Or(var left, _):
                WalkAst(left, mutations);
                break;

            case PredicateAst.Not(var inner):
            {
                var innerMutations = new List<StateMutation>();
                WalkAst(inner, innerMutations);
                foreach (var m in innerMutations)
                    mutations.Add(new StateMutation.SetNotPredicate(m));
                break;
            }

            case PredicateAst.Comparison comparison:
                ExtractFromComparison(comparison, mutations);
                break;

            case PredicateAst.FunctionCall functionCall:
                ExtractFromFunctionCall(functionCall, mutations);
                break;

            case PredicateAst.DefaultLiteral:
                break;

            default:
                break;
        }
    }

    private static void ExtractFromComparison(PredicateAst.Comparison comparison, List<StateMutation> mutations)
    {
        var op = comparison.Op;

        // <= and < mean "stay below X" — no mutation needed to make predicate true by raising value
        if (op is ComparisonOp.LtEq or ComparisonOp.Lt)
            return;

        // NotEq — skip
        if (op is ComparisonOp.NotEq)
            return;

        // Left must be a function call, right must be an int literal
        if (comparison.Left is not PredicateAst.FunctionCall call)
        {
            WalkAst(comparison.Left, mutations);
            return;
        }

        if (comparison.Right is not PredicateAst.IntLiteral(var rhsValue))
            return;

        // > means value + 1 to satisfy strictly greater-than
        var value = op is ComparisonOp.Gt ? (int)rhsValue + 1 : (int)rhsValue;

        ExtractFromNamedFunction(call.Name, call.Args, value, mutations);
    }

    private static void ExtractFromFunctionCall(PredicateAst.FunctionCall call, List<StateMutation> mutations)
    {
        // Boolean functions used without comparison (e.g. isQuestComplete(id), questFlag(id, bit))
        ExtractFromNamedFunction(call.Name, call.Args, 0, mutations);
    }

    private static void ExtractFromNamedFunction(
        string name,
        IReadOnlyList<PredicateAst> args,
        int comparedValue,
        List<StateMutation> mutations)
    {
        switch (name)
        {
            case "questSequence":
            {
                if (args.Count >= 1 && args[0] is PredicateAst.IntLiteral(var id))
                    mutations.Add(new StateMutation.SetQuestSequence(new QuestId((uint)id), comparedValue));
                break;
            }

            case "isQuestComplete":
            {
                if (args.Count >= 1 && args[0] is PredicateAst.IntLiteral(var id))
                    mutations.Add(new StateMutation.SetQuestComplete(new QuestId((uint)id)));
                break;
            }

            case "isQuestAccepted":
            {
                if (args.Count >= 1 && args[0] is PredicateAst.IntLiteral(var id))
                    mutations.Add(new StateMutation.SetQuestAccepted(new QuestId((uint)id)));
                break;
            }

            case "playerZone":
            {
                mutations.Add(new StateMutation.SetPlayerZone(comparedValue));
                break;
            }

            case "playerNear":
            {
                if (args.Count >= 2
                    && args[0] is PredicateAst.PositionLiteral(var x, var y, var z)
                    && args[1] is PredicateAst.IntLiteral(var radius))
                {
                    mutations.Add(new StateMutation.SetPlayerNear(x, y, z, (float)radius));
                }
                break;
            }

            case "questFlag":
            {
                if (args.Count >= 2
                    && args[0] is PredicateAst.IntLiteral(var id)
                    && args[1] is PredicateAst.IntLiteral(var bit))
                {
                    mutations.Add(new StateMutation.SetQuestFlag(new QuestId((uint)id), (int)bit, true));
                }
                break;
            }

            case "questVariable":
            {
                if (args.Count >= 2
                    && args[0] is PredicateAst.IntLiteral(var id)
                    && args[1] is PredicateAst.IntLiteral(var index))
                {
                    mutations.Add(new StateMutation.SetQuestVariable(
                        new QuestId((uint)id), (int)index, comparedValue, Nibble.Whole));
                }
                break;
            }

            case "questVariableLow":
            {
                if (args.Count >= 2
                    && args[0] is PredicateAst.IntLiteral(var id)
                    && args[1] is PredicateAst.IntLiteral(var index))
                {
                    mutations.Add(new StateMutation.SetQuestVariable(
                        new QuestId((uint)id), (int)index, comparedValue, Nibble.Low));
                }
                break;
            }

            case "questVariableHigh":
            {
                if (args.Count >= 2
                    && args[0] is PredicateAst.IntLiteral(var id)
                    && args[1] is PredicateAst.IntLiteral(var index))
                {
                    mutations.Add(new StateMutation.SetQuestVariable(
                        new QuestId((uint)id), (int)index, comparedValue, Nibble.High));
                }
                break;
            }

            case "isAttuned":
            {
                if (args.Count >= 1 && args[0] is PredicateAst.IntLiteral(var id))
                    mutations.Add(new StateMutation.SetAttuned((uint)id));
                break;
            }

            case "playerHasItem":
            {
                if (args.Count >= 1 && args[0] is PredicateAst.IntLiteral(var id))
                {
                    var qty = args.Count >= 2 && args[1] is PredicateAst.IntLiteral(var q) ? (int)q : 1;
                    mutations.Add(new StateMutation.SetPlayerHasItem((uint)id, qty));
                }
                break;
            }

            case "objectExistsInRange":
            {
                if (args.Count >= 2
                    && args[0] is PredicateAst.IntLiteral(var dataId)
                    && args[1] is PredicateAst.IntLiteral(var range))
                {
                    mutations.Add(new StateMutation.SetObjectExistsInRange((uint)dataId, (float)range));
                }
                break;
            }

            case "npcExistsNearby":
            case "objectExists":
            {
                if (args.Count >= 1 && args[0] is PredicateAst.IntLiteral(var dataId))
                    mutations.Add(new StateMutation.SetNpcExistsNearby((uint)dataId));
                break;
            }

            case "isSlotEquipped":
            {
                if (args.Count >= 1 && args[0] is PredicateAst.IntLiteral(var slotIndex))
                    mutations.Add(new StateMutation.SetSlotEquipped((int)slotIndex, 1));
                break;
            }

            case "playerHasEquipped":
            {
                if (args.Count >= 1 && args[0] is PredicateAst.IntLiteral(var itemId))
                    mutations.Add(new StateMutation.SetItemEquipped((uint)itemId));
                break;
            }

            case "playerInCombat":
                mutations.Add(new StateMutation.SetInCombat(true));
                break;

            case "jobGearsetExists":
            {
                if (args.Count >= 1 && args[0] is PredicateAst.IntLiteral(var jobId))
                    mutations.Add(new StateMutation.SetGearsetExistsForJob((uint)jobId));
                break;
            }

            case "inventoryHasCoffers":
                mutations.Add(new StateMutation.SetHasCoffers(true));
                break;

            case "isAetherCurrentAttuned":
            {
                if (args.Count >= 1 && args[0] is PredicateAst.IntLiteral(var id))
                    mutations.Add(new StateMutation.SetAetherCurrentAttuned((uint)id));
                break;
            }

            // Job predicates — empty list (job set via initial state or ChangeJobStep)
            case "isDiscipleOfWar":
            case "isDiscipleOfMagic":
            case "isPlayerJob":
            case "playerJobId":
                break;

            // Unknown function — return empty (no crash)
            default:
                break;
        }
    }
}
