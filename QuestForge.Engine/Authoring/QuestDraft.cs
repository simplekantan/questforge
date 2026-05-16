using QuestForge.Adapters.Types;
using QuestForge.Schema;

namespace QuestForge.Engine.Authoring;

public sealed class QuestDraft
{
    private readonly List<DraftStep> _steps = new();

    public QuestId QuestId { get; }
    public string? QuestName { get; set; }
    public string Category { get; set; } = "msq";
    public string Expansion { get; set; } = "arr";
    public string? LastVerifiedPatch { get; set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset LastModifiedAt { get; private set; }
    public IReadOnlyList<DraftStep> Steps => _steps;

    public QuestDraft(QuestId questId, DateTimeOffset createdAt)
    {
        QuestId = questId;
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
    {
        return _steps.FirstOrDefault(s => s.StepId == stepId);
    }

    /// <summary>
    /// Convert draft to a schema-valid QuestDefinition.
    /// Throws DraftSerializationException if any step lacks a Raw value.
    /// Steps are grouped into QuestSequence objects by SequenceNumber, ascending.
    /// </summary>
    public QuestDefinition ToQuestDefinition()
    {
        // Check for null Raw
        var nullStep = _steps.FirstOrDefault(s => s.Raw is null);
        if (nullStep is not null)
            throw new DraftSerializationException(
                $"Step '{nullStep.StepId}' has no Raw value — confirm the step in the record modal before exporting.");

        var grouped = _steps
            .GroupBy(s => s.SequenceNumber)
            .OrderBy(g => g.Key)
            .Select(g => new QuestSequence
            {
                Sequence = g.Key,
                Steps = g.Select(s => s.Raw!).ToArray()
            })
            .ToArray();

        return new QuestDefinition
        {
            SchemaVersion = "1.0.0",
            Id = QuestId.Value,
            Name = QuestName ?? "",
            Expansion = Expansion,
            Category = Category,
            Enabled = true,
            SupportStatus = new SupportStatus { Implementation = "partial", KnownIssues = [] },
            LastVerifiedPatch = LastVerifiedPatch ?? "unknown",
            Requirements = new Requirements(),
            AcceptFrom = InferAcceptFrom(),
            Sequences = grouped
        };
    }

    private NpcLocation? InferAcceptFrom()
    {
        var acceptStep = _steps.FirstOrDefault(s => s.Raw is AcceptStep);
        return (acceptStep?.Raw as AcceptStep)?.Target;
    }
}
