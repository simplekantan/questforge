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

        ValidateSteps(steps, errors, warnings);

        // E4: No accept step (quest-only)
        var hasAcceptStep = steps.Any(s => s.Raw is AcceptStep);
        if (!hasAcceptStep)
        {
            errors.Add(new DraftValidationError("E4",
                "Draft does not contain an 'accept' step. Add an AcceptStep to define where the quest is picked up."));
        }

        // W5: A sequence group exists with zero steps (can't happen via normal Add/Remove, so rarely fires)
        // Per spec: numeric gaps do NOT trigger W5 — only zero-step groups trigger W5.
        // Since our grouping is driven by actual steps, empty groups don't exist in normal operation.
        // We explicitly do NOT check for numeric gaps here.

        // W6: empty / placeholder quest name (quest-only)
        var name = draft.QuestName?.Trim();
        if (string.IsNullOrEmpty(name) || QuestNamePlaceholders.Contains(name))
        {
            warnings.Add(new DraftValidationWarning("W6",
                "Quest name is empty or a placeholder ('TODO'). Set QuestDraft.QuestName before export."));
        }

        return (errors, warnings);
    }

    private static void ValidateSteps(
        IReadOnlyList<DraftStep> steps,
        List<DraftValidationError> errors,
        List<DraftValidationWarning> warnings)
    {
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

        // E9: UseEmoteStep with EmoteId == 0
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].Raw is UseEmoteStep ue && ue.EmoteId == 0)
            {
                errors.Add(new DraftValidationError("E9",
                    $"Step '{steps[i].StepId}' is a UseEmoteStep with EmoteId == 0.",
                    [i]));
            }
        }

        // E10: UseEmoteStep with TargetNpcId == 0 (null is allowed; explicit zero is invalid)
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].Raw is UseEmoteStep ue && ue.TargetNpcId == 0)
            {
                errors.Add(new DraftValidationError("E10",
                    $"Step '{steps[i].StepId}' is a UseEmoteStep with TargetNpcId == 0.",
                    [i]));
            }
        }

        // E11: SayChatMessageStep with empty Message
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].Raw is SayChatMessageStep sc && string.IsNullOrEmpty(sc.Message))
            {
                errors.Add(new DraftValidationError("E11",
                    $"Step '{steps[i].StepId}' is a SayChatMessageStep with an empty Message.",
                    [i]));
            }
        }

        // E12: SayChatMessageStep with TargetNpcId == 0 (null is allowed; explicit zero is invalid)
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].Raw is SayChatMessageStep sc && sc.TargetNpcId == 0)
            {
                errors.Add(new DraftValidationError("E12",
                    $"Step '{steps[i].StepId}' is a SayChatMessageStep with TargetNpcId == 0.",
                    [i]));
            }
        }

        // E13: UseItemStep with ItemId == 0
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].Raw is UseItemStep ui && ui.ItemId == 0)
            {
                errors.Add(new DraftValidationError("E13",
                    $"Step '{steps[i].StepId}' is a UseItemStep with ItemId == 0.",
                    [i]));
            }
        }

        // E21: DutyStep(kind:"duty") with ContentFinderConditionId == 0
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].Raw is DutyStep { Kind: "duty", ContentFinderConditionId: 0 })
            {
                errors.Add(new DraftValidationError("E21",
                    $"Step '{steps[i].StepId}' has ContentFinderConditionId=0 which is invalid.",
                    [i]));
            }
        }

        // E22: DutyStep with EntryTargetId == 0 (null is OK; explicit 0 is invalid)
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].Raw is DutyStep { EntryTargetId: 0 })
            {
                errors.Add(new DraftValidationError("E22",
                    $"Step '{steps[i].StepId}' has EntryTargetId=0 which is invalid. " +
                    "Use null to omit entry target, or provide a valid NPC DataId.",
                    [i]));
            }
        }

        // E23: PurchaseItemStep with both VendorCategory and GcCategory/GcRankTier set
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].Raw is PurchaseItemStep ps23
                && ps23.VendorCategory.HasValue
                && (ps23.GcCategory.HasValue || ps23.GcRankTier.HasValue))
            {
                errors.Add(new DraftValidationError("E23",
                    $"Step '{steps[i].StepId}' has both VendorCategory and GcCategory/GcRankTier set. " +
                    "These target different vendor UI types and are mutually exclusive.",
                    [i]));
            }
        }

        // E24: PurchaseItemStep with VendorCategory < 0
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].Raw is PurchaseItemStep ps24 && ps24.VendorCategory < 0)
            {
                errors.Add(new DraftValidationError("E24",
                    $"Step '{steps[i].StepId}' has VendorCategory={ps24.VendorCategory} which is negative.",
                    [i]));
            }
        }

        // E14: UseItemStep with TargetNpcId == 0 (null is allowed; explicit zero is invalid)
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].Raw is UseItemStep ui && ui.TargetNpcId == 0)
            {
                errors.Add(new DraftValidationError("E14",
                    $"Step '{steps[i].StepId}' is a UseItemStep with TargetNpcId == 0.",
                    [i]));
            }
        }

        // E15: UseItemStep with both TargetNpcId and TargetPosition set — ambiguous target
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].Raw is UseItemStep ui && ui.TargetNpcId.HasValue && ui.TargetPosition is not null)
            {
                errors.Add(new DraftValidationError("E15",
                    $"Step '{steps[i].StepId}' is a UseItemStep with both TargetNpcId and TargetPosition set; use one or the other.",
                    [i]));
            }
        }

        // E16: EquipGearForQuestStep with empty ItemIds
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].Raw is EquipGearForQuestStep eg && eg.ItemIds.Length == 0)
            {
                errors.Add(new DraftValidationError("E16",
                    $"Step '{steps[i].StepId}' is an EquipGearForQuestStep with empty ItemIds array.",
                    [i]));
            }
        }

        // E17: EquipGearForQuestStep with zero value in ItemIds
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].Raw is EquipGearForQuestStep eg)
            {
                for (var j = 0; j < eg.ItemIds.Length; j++)
                {
                    if (eg.ItemIds[j] == 0)
                    {
                        errors.Add(new DraftValidationError("E17",
                            $"Step '{steps[i].StepId}' is an EquipGearForQuestStep with ItemIds containing a zero value at index {j}.",
                            [i]));
                    }
                }
            }
        }

        // E18: ChangeJobStep with JobId == 0
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].Raw is ChangeJobStep cj && cj.JobId == 0)
            {
                errors.Add(new DraftValidationError("E18",
                    $"Step '{steps[i].StepId}' is a ChangeJobStep with JobId == 0.",
                    [i]));
            }
        }

        // E19: InteractObjectStep with InteractableId == 0
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].Raw is InteractObjectStep io && io.InteractableId == 0)
            {
                errors.Add(new DraftValidationError("E19",
                    $"Step '{steps[i].StepId}' is an InteractObjectStep with InteractableId == 0.",
                    [i]));
            }
        }

        // E20: PickupItemStep with InteractableId == 0
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].Raw is PickupItemStep pi && pi.InteractableId == 0)
            {
                errors.Add(new DraftValidationError("E20",
                    $"Step '{steps[i].StepId}' is a PickupItemStep with InteractableId == 0.",
                    [i]));
            }
        }

        // W1: Step has no Expect
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (step.Raw is null) continue;
            if (step.Raw.Expect is null
                && step.Raw is not UseActionStep
                and not UseEmoteStep
                and not SayChatMessageStep
                and not UseItemStep
                and not EquipGearForQuestStep
                and not ChangeJobStep
                && step.Raw is not DutyStep { Kind: "duty" })
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

        // W8: UseEmoteStep with no Expect — engine spin-loops without one (UE7 stronger than W1)
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (step.Raw is UseEmoteStep ue && ue.Expect is null)
            {
                warnings.Add(new DraftValidationWarning("W8",
                    $"Step '{step.StepId}' is a UseEmoteStep with no 'expect' predicate — without it the engine will spin-loop re-emitting the emote. Add an expect predicate.",
                    [i]));
            }
        }

        // W9: SayChatMessageStep with no Expect — engine spin-loops without one (SC7 stronger than W1)
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (step.Raw is SayChatMessageStep sc && sc.Expect is null)
            {
                warnings.Add(new DraftValidationWarning("W9",
                    $"Step '{step.StepId}' is a SayChatMessageStep with no 'expect' predicate — without it the engine will spin-loop re-emitting the message. Add an expect predicate.",
                    [i]));
            }
        }

        // W10: UseItemStep with no Expect — engine spin-loops without one
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (step.Raw is UseItemStep ui && ui.Expect is null)
            {
                warnings.Add(new DraftValidationWarning("W10",
                    $"Step '{step.StepId}' is a UseItemStep with no 'expect' predicate — without it the engine will spin-loop re-emitting the action. Add an expect predicate.",
                    [i]));
            }
        }

        // W11: DutyStep(kind:"duty") with no Expect — engine spin-loops without one
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (step.Raw is DutyStep { Kind: "duty" } dt && dt.Expect is null)
            {
                warnings.Add(new DraftValidationWarning("W11",
                    $"Step '{step.StepId}' is a DutyStep(kind:duty) with no 'expect' predicate " +
                    "-- without it the engine will spin-loop re-dispatching EnterDuty. " +
                    "Add an expect predicate (e.g. questSequence).",
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
    }

    /// <summary>
    /// Validate a fragment draft. Runs shared step-level rules (E1-E22, W1-W11)
    /// plus fragment-specific rules (EF1, EF2, WF1).
    /// Does NOT run quest-specific rules (E4, W6).
    /// </summary>
    public (IReadOnlyList<DraftValidationError> Errors, IReadOnlyList<DraftValidationWarning> Warnings) ValidateFragment(FragmentDraft draft)
    {
        var errors = new List<DraftValidationError>();
        var warnings = new List<DraftValidationWarning>();

        // EF1: FragmentId is null or whitespace
        if (string.IsNullOrWhiteSpace(draft.FragmentId))
        {
            errors.Add(new DraftValidationError("EF1",
                "FragmentId is empty or whitespace. Set a valid fragment identifier."));
            return (errors, warnings);
        }

        // EF2: FragmentId format validation — ^[a-z][a-z0-9/-]*[a-z0-9]$ with min length 3
        if (draft.FragmentId.Length < 3 || !System.Text.RegularExpressions.Regex.IsMatch(
                draft.FragmentId, @"^[a-z][a-z0-9/\-]*[a-z0-9]$")
            || draft.FragmentId.Contains("//"))
        {
            errors.Add(new DraftValidationError("EF2",
                $"FragmentId '{draft.FragmentId}' does not match the required format " +
                "(lowercase slug, minimum 3 chars, only letters/digits/hyphens/slashes, no trailing slash)."));
        }

        ValidateSteps(draft.Steps, errors, warnings);

        // WF1: No steps
        if (draft.Steps.Count == 0)
        {
            warnings.Add(new DraftValidationWarning("WF1",
                "Fragment draft has no steps. Add steps before exporting."));
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
