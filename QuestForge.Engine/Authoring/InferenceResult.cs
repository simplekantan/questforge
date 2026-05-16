namespace QuestForge.Engine.Authoring;

public sealed record InferenceResult(
    string StepType,
    string SuggestedStepId,
    string? SuggestedExpect,
    Confidence Confidence,
    InferredFrom InferredFrom,
    string? Notes)
{
    public static InferenceResult Empty { get; } = new(
        StepType: "",
        SuggestedStepId: "",
        SuggestedExpect: null,
        Confidence: Confidence.Low,
        InferredFrom: InferredFrom.None,
        Notes: null);
}
