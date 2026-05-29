using QuestForge.Predicates;
using QuestForge.Schema;

namespace QuestForge.Engine.Authoring;

public sealed record DraftValidationError(string Code, string Message, int[]? StepIndices = null);
public sealed record DraftValidationWarning(string Code, string Message, int[]? StepIndices = null);

public sealed class DraftValidator
{
    private static readonly HashSet<string> QuestNamePlaceholders =
        new(StringComparer.OrdinalIgnoreCase) { "TODO" };

    public (IReadOnlyList<DraftValidationError> Errors, IReadOnlyList<DraftValidationWarning> Warnings) Validate(QuestDraft draft)
    {
        var errors = new List<DraftValidationError>();
        var warnings = new List<DraftValidationWarning>();

        var steps = draft.Steps;

        // E1: Duplicate stepId
        var duplicates = steps
            .GroupBy(s => s.StepId)
            .Where(g => g.Count() > 1)
            .ToList();
        if (duplicates.Count > 0)
        {
            foreach (var group in duplicates)
            {
                errors.Add(new DraftValidationError("E1",
                    $"Duplicate stepId '{group.Key}' found in draft."));
            }
        }

        // E2: Raw == null
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].Raw is null)
            {
                errors.Add(new DraftValidationError("E2",
                    $"Step '{steps[i].StepId}' at index {i} has no Raw value (modal abandoned mid-record).",
                    [i]));
            }
        }

        // E3 / E5: full predicate parser + checker against Expect and SkipIf
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (step.Raw is null) continue; // already flagged by E2

            foreach (var pred in ExtractPredicates(step.Raw.Expect))
                ValidatePredicate(pred, i, step.StepId, errors);

            foreach (var pred in ExtractPredicates(step.Raw.SkipIf))
                ValidatePredicate(pred, i, step.StepId, errors);
        }

        // E4: No accept step
        var hasAcceptStep = steps.Any(s => s.Raw is AcceptStep);
        if (!hasAcceptStep)
        {
            errors.Add(new DraftValidationError("E4",
                "Draft does not contain an 'accept' step. Add an AcceptStep to define where the quest is picked up."));
        }

        // E6: TalkStep with NpcId == 0
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].Raw is TalkStep talkStep && talkStep.Target?.NpcId == 0)
            {
                errors.Add(new DraftValidationError("E6",
                    $"Step '{steps[i].StepId}' is a TalkStep with Target.NpcId == 0.",
                    [i]));
            }
        }

        // E7: UseActionStep with ActionId == 0
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].Raw is UseActionStep ua && ua.ActionId == 0)
            {
                errors.Add(new DraftValidationError("E7",
                    $"Step '{steps[i].StepId}' is a UseActionStep with ActionId == 0.",
                    [i]));
            }
        }

        // E8: UseActionStep with TargetNpcId == 0 (null is allowed; explicit zero is invalid)
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].Raw is UseActionStep ua && ua.TargetNpcId == 0)
            {
                errors.Add(new DraftValidationError("E8",
                    $"Step '{steps[i].StepId}' is a UseActionStep with TargetNpcId == 0.",
                    [i]));
            }
        }

        // W1: Step has no Expect
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (step.Raw is null) continue;
            if (step.Raw.Expect is null && step.Raw is not UseActionStep)
            {
                warnings.Add(new DraftValidationWarning("W1",
                    $"Step '{step.StepId}' has no 'expect' predicate. Consider adding one for reliability.",
                    [i]));
            }
        }

        // W7: UseActionStep with no Expect — engine spin-loops without one (UA7 stronger than W1)
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (step.Raw is UseActionStep ua && ua.Expect is null)
            {
                warnings.Add(new DraftValidationWarning("W7",
                    $"Step '{step.StepId}' is a UseActionStep with no 'expect' predicate — without it the engine will spin-loop re-emitting the action. Add an expect predicate.",
                    [i]));
            }
        }

        // W2: Step has no notes and was not manually inferred
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].Notes is null && steps[i].InferredFrom == InferredFrom.None)
            {
                warnings.Add(new DraftValidationWarning("W2",
                    $"Step '{steps[i].StepId}' has no notes and InferredFrom=None. Consider adding context.",
                    [i]));
            }
        }

        // W3: Last step in last sequence is a TravelStep
        if (steps.Count > 0)
        {
            var lastStep = steps[^1];
            if (lastStep.Raw is TravelStep)
            {
                warnings.Add(new DraftValidationWarning("W3",
                    $"The last step '{lastStep.StepId}' is a TravelStep, which is usually unintentional."));
            }
        }

        // W4: Two consecutive TalkSteps with same NpcId (might be a redundant recording)
        for (var i = 0; i < steps.Count - 1; i++)
        {
            if (steps[i].Raw is not TalkStep || steps[i + 1].Raw is not TalkStep) continue;
            var npc1 = GetNpcId(steps[i].Raw);
            var npc2 = GetNpcId(steps[i + 1].Raw);
            if (npc1.HasValue && npc2.HasValue && npc1.Value == npc2.Value)
            {
                warnings.Add(new DraftValidationWarning("W4",
                    $"Steps '{steps[i].StepId}' and '{steps[i + 1].StepId}' have the same NpcId — might be a redundant recording.",
                    [i, i + 1]));
            }
        }

        // W5: A sequence group exists with zero steps (can't happen via normal Add/Remove, so rarely fires)
        // Per spec: numeric gaps do NOT trigger W5 — only zero-step groups trigger W5.
        // Since our grouping is driven by actual steps, empty groups don't exist in normal operation.
        // We explicitly do NOT check for numeric gaps here.

        // W6: empty / placeholder quest name
        var name = draft.QuestName?.Trim();
        if (string.IsNullOrEmpty(name) || QuestNamePlaceholders.Contains(name))
        {
            warnings.Add(new DraftValidationWarning("W6",
                "Quest name is empty or a placeholder ('TODO'). Set QuestDraft.QuestName before export."));
        }

        return (errors, warnings);
    }

    private static void ValidatePredicate(string predicate, int stepIndex, string stepId,
                                          List<DraftValidationError> errors)
    {
        var result = PredicateParser.Parse(predicate);

        if (result.Errors.Count > 0)
        {
            var pe = result.Errors[0];
            var code = pe.Code == "unknown-function" ? "E5" : "E3";
            var msg = code == "E5"
                ? $"Step '{stepId}' predicate '{predicate}' references an unknown function. {pe.Message}{(pe.Suggestion is null ? "" : $" Did you mean '{pe.Suggestion}'?")}"
                : $"Step '{stepId}' predicate '{predicate}' failed to parse: {pe.Message}";
            errors.Add(new DraftValidationError(code, msg, [stepIndex]));
            return;
        }

        if (result.Ast is not null)
        {
            var checkerErrors = PredicateChecker.Check(result.Ast);
            if (checkerErrors.Count > 0)
            {
                var ce = checkerErrors[0];
                var code = ce.Code == "unknown-function" ? "E5" : "E3";
                var msg = code == "E5"
                    ? $"Step '{stepId}' predicate '{predicate}' references an unknown function. {ce.Message}{(ce.Suggestion is null ? "" : $" Did you mean '{ce.Suggestion}'?")}"
                    : $"Step '{stepId}' predicate '{predicate}': {ce.Message}";
                errors.Add(new DraftValidationError(code, msg, [stepIndex]));
            }
        }
    }

    private static IReadOnlyList<string> ExtractPredicates(ExpectValue? expect) => expect switch
    {
        PredicateExpect p => [p.Predicate],
        AllExpect a       => a.All,
        AnyExpect a       => a.Any,
        _                 => []
    };

    private static uint? GetNpcId(Step? step)
    {
        return step switch
        {
            TalkStep t => t.Target?.NpcId,
            TurnInStep ti => ti.Target?.NpcId,
            AcceptStep a => a.Target?.NpcId,
            _ => null
        };
    }
}
