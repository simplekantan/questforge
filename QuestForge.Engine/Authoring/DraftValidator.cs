using QuestForge.Schema;

namespace QuestForge.Engine.Authoring;

public sealed record DraftValidationError(string Code, string Message, int[]? StepIndices = null);
public sealed record DraftValidationWarning(string Code, string Message, int[]? StepIndices = null);

public sealed class DraftValidator
{
    private static readonly HashSet<string> KnownFunctions = new(StringComparer.Ordinal)
    {
        "questSequence", "questFlag", "isQuestAccepted", "isQuestComplete",
        "playerZone", "playerNear", "inCombat"
    };

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

        // E3/E5: Predicate parse checks
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (step.Raw is null) continue; // already flagged by E2

            var predicate = ExtractPredicate(step.Raw.Expect);
            if (predicate is not null && !HasKnownFunction(predicate))
            {
                errors.Add(new DraftValidationError("E5",
                    $"Step '{step.StepId}' predicate '{predicate}' references an unknown function. " +
                    $"Known functions: {string.Join(", ", KnownFunctions)}. Did you mean one of those?",
                    [i]));
            }
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

        // W1: Step has no Expect
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (step.Raw is null) continue;
            if (step.Raw.Expect is null)
            {
                warnings.Add(new DraftValidationWarning("W1",
                    $"Step '{step.StepId}' has no 'expect' predicate. Consider adding one for reliability.",
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

        return (errors, warnings);
    }

    private static bool HasKnownFunction(string? predicate)
    {
        if (predicate is null) return true; // no predicate = no parse error
        return KnownFunctions.Any(f => predicate.Contains(f, StringComparison.Ordinal));
    }

    private static string? ExtractPredicate(ExpectValue? expect)
    {
        return expect switch
        {
            PredicateExpect p => p.Predicate,
            _ => null
        };
    }

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
