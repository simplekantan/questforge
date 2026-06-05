using QuestForge.Schema;

namespace QuestForge.Engine.Authoring;

/// <summary>
/// Mutable draft for a fragment definition (parallel to QuestDraft for quest definitions).
/// Steps are managed in a flat list (no sequence grouping).
/// </summary>
public sealed class FragmentDraft
{
    private readonly List<DraftStep> _steps = new();

    public string FragmentId { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset LastModifiedAt { get; private set; }
    public IReadOnlyList<DraftStep> Steps => _steps;

    public FragmentDraft(string fragmentId, DateTimeOffset createdAt)
    {
        FragmentId = fragmentId;
        CreatedAt = createdAt;
        LastModifiedAt = createdAt;
    }

    /// <exception cref="InvalidOperationException">Thrown when stepId already exists in the draft.</exception>
    public void AddStep(DraftStep step, DateTimeOffset now)
    {
        if (_steps.Any(s => s.StepId == step.StepId))
            throw new InvalidOperationException($"A step with id '{step.StepId}' already exists in this draft.");

        _steps.Add(step);
        LastModifiedAt = now;
    }

    public bool RemoveStep(string stepId, DateTimeOffset now)
    {
        var index = _steps.FindIndex(s => s.StepId == stepId);
        if (index < 0)
            return false;

        _steps.RemoveAt(index);
        LastModifiedAt = now;
        return true;
    }

    public bool ReplaceStep(string stepId, DraftStep newStep, DateTimeOffset now)
    {
        var index = _steps.FindIndex(s => s.StepId == stepId);
        if (index < 0)
            return false;

        _steps[index] = newStep;
        LastModifiedAt = now;
        return true;
    }

    public DraftStep? GetStep(string stepId)
        => _steps.FirstOrDefault(s => s.StepId == stepId);

    public bool MoveStepUp(string stepId, DateTimeOffset now)
    {
        var index = _steps.FindIndex(s => s.StepId == stepId);
        if (index < 1) return false;

        (_steps[index], _steps[index - 1]) = (_steps[index - 1], _steps[index]);
        LastModifiedAt = now;
        return true;
    }

    public bool MoveStepDown(string stepId, DateTimeOffset now)
    {
        var index = _steps.FindIndex(s => s.StepId == stepId);
        if (index < 0 || index >= _steps.Count - 1) return false;

        (_steps[index], _steps[index + 1]) = (_steps[index + 1], _steps[index]);
        LastModifiedAt = now;
        return true;
    }

    /// <summary>
    /// Convert draft to a schema-valid FragmentDefinition.
    /// Throws DraftSerializationException if any step lacks a Raw value.
    /// </summary>
    public FragmentDefinition ToFragmentDefinition()
    {
        var nullStep = _steps.FirstOrDefault(s => s.Raw is null);
        if (nullStep is not null)
            throw new DraftSerializationException(
                $"Step '{nullStep.StepId}' has no Raw value — confirm the step in the record modal before exporting.");

        return new FragmentDefinition
        {
            SchemaVersion = "1.0.0",
            FragmentId = FragmentId,
            Parameters = [],
            Steps = _steps.Select(s => s.Raw!).ToArray()
        };
    }

    /// <summary>
    /// Test-only escape hatch: construct a draft whose internal step list is
    /// not guarded by AddStep's uniqueness invariant. Required for E1 (duplicate
    /// stepId) positive testing and for fragment drafts loaded from corrupted files.
    /// </summary>
    internal static FragmentDraft CreateForTest(
        string fragmentId, DateTimeOffset createdAt,
        IEnumerable<DraftStep> steps)
    {
        var draft = new FragmentDraft(fragmentId, createdAt);
        draft._steps.AddRange(steps);
        return draft;
    }
}
