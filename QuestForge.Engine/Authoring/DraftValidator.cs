namespace QuestForge.Engine.Authoring;

public sealed record DraftValidationError(string Code, string Message, int[]? StepIndices = null);
public sealed record DraftValidationWarning(string Code, string Message, int[]? StepIndices = null);

public sealed class DraftValidator
{
    public (IReadOnlyList<DraftValidationError> Errors, IReadOnlyList<DraftValidationWarning> Warnings) Validate(QuestDraft draft)
    {
        throw new NotImplementedException();
    }
}
