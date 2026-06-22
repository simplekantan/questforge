using QuestForge.Adapters.Types;
using QuestForge.Schema;

namespace QuestForge.Engine.Authoring;

public sealed class QuestDraft
{
    private readonly List<DraftStep> _steps = new();
    private readonly List<DraftStep> _epilogueSteps = new();

    public QuestId QuestId { get; }
    public string? QuestName { get; set; }
    public string Category { get; set; } = "msq";
    public string Expansion { get; set; } = "arr";
    public string? LastVerifiedPatch { get; set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset LastModifiedAt { get; private set; }
    public IReadOnlyList<DraftStep> Steps => _steps;
    public IReadOnlyList<DraftStep> EpilogueSteps => _epilogueSteps;

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

    public void InsertStepAfter(string afterStepId, DraftStep step, DateTimeOffset now)
    {
        if (_steps.Any(s => s.StepId == step.StepId))
            throw new InvalidOperationException($"A step with id '{step.StepId}' already exists in this draft.");

        var index = _steps.FindIndex(s => s.StepId == afterStepId);
        if (index < 0)
            throw new InvalidOperationException($"Step '{afterStepId}' not found in this draft.");

        _steps.Insert(index + 1, step);
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

    public bool MoveStepUp(string stepId, DateTimeOffset now)
    {
        var index = _steps.FindIndex(s => s.StepId == stepId);
        if (index < 0) return false;

        var step = _steps[index];
        var sameSeqIndices = SameSequenceIndices(step.SequenceNumber);
        var pos = sameSeqIndices.IndexOf(index);
        if (pos < 1) return false;

        var swapIndex = sameSeqIndices[pos - 1];
        _steps[index] = _steps[swapIndex];
        _steps[swapIndex] = step;
        LastModifiedAt = now;
        return true;
    }

    public bool MoveStepDown(string stepId, DateTimeOffset now)
    {
        var index = _steps.FindIndex(s => s.StepId == stepId);
        if (index < 0) return false;

        var step = _steps[index];
        var sameSeqIndices = SameSequenceIndices(step.SequenceNumber);
        var pos = sameSeqIndices.IndexOf(index);
        if (pos < 0 || pos >= sameSeqIndices.Count - 1) return false;

        var swapIndex = sameSeqIndices[pos + 1];
        _steps[index] = _steps[swapIndex];
        _steps[swapIndex] = step;
        LastModifiedAt = now;
        return true;
    }

    private List<int> SameSequenceIndices(int sequenceNumber)
    {
        var indices = new List<int>();
        for (var i = 0; i < _steps.Count; i++)
            if (_steps[i].SequenceNumber == sequenceNumber)
                indices.Add(i);
        return indices;
    }

    // ---- Epilogue step management ----

    public void AddEpilogueStep(DraftStep step, DateTimeOffset now)
    {
        if (_epilogueSteps.Any(s => s.StepId == step.StepId))
            throw new InvalidOperationException($"An epilogue step with id '{step.StepId}' already exists.");

        _epilogueSteps.Add(step);
        LastModifiedAt = now;
    }

    public bool RemoveEpilogueStep(string stepId, DateTimeOffset now)
    {
        var index = _epilogueSteps.FindIndex(s => s.StepId == stepId);
        if (index < 0) return false;

        _epilogueSteps.RemoveAt(index);
        LastModifiedAt = now;
        return true;
    }

    public bool ReplaceEpilogueStep(string stepId, DraftStep newStep, DateTimeOffset now)
    {
        var index = _epilogueSteps.FindIndex(s => s.StepId == stepId);
        if (index < 0) return false;

        _epilogueSteps[index] = newStep;
        LastModifiedAt = now;
        return true;
    }

    public bool MoveEpilogueStepUp(string stepId, DateTimeOffset now)
    {
        var index = _epilogueSteps.FindIndex(s => s.StepId == stepId);
        if (index < 1) return false;

        (_epilogueSteps[index], _epilogueSteps[index - 1]) = (_epilogueSteps[index - 1], _epilogueSteps[index]);
        LastModifiedAt = now;
        return true;
    }

    public bool MoveEpilogueStepDown(string stepId, DateTimeOffset now)
    {
        var index = _epilogueSteps.FindIndex(s => s.StepId == stepId);
        if (index < 0 || index >= _epilogueSteps.Count - 1) return false;

        (_epilogueSteps[index], _epilogueSteps[index + 1]) = (_epilogueSteps[index + 1], _epilogueSteps[index]);
        LastModifiedAt = now;
        return true;
    }

    public void InsertEpilogueStepAfter(string afterStepId, DraftStep step, DateTimeOffset now)
    {
        if (_epilogueSteps.Any(s => s.StepId == step.StepId))
            throw new InvalidOperationException($"An epilogue step with id '{step.StepId}' already exists.");

        var index = _epilogueSteps.FindIndex(s => s.StepId == afterStepId);
        if (index < 0)
            throw new InvalidOperationException($"Epilogue step '{afterStepId}' not found.");

        _epilogueSteps.Insert(index + 1, step);
        LastModifiedAt = now;
    }

    /// <summary>
    /// Test-only escape hatch: construct a draft whose internal step list is
    /// not guarded by AddStep's uniqueness invariant. Required for E1 (duplicate
    /// stepId) positive testing and for any future test that simulates a draft
    /// loaded from a corrupted file.
    /// </summary>
    internal static QuestDraft CreateForTest(
        QuestId questId,
        DateTimeOffset createdAt,
        IEnumerable<DraftStep> steps,
        string? questName = null)
    {
        var draft = new QuestDraft(questId, createdAt) { QuestName = questName };
        draft._steps.AddRange(steps);
        return draft;
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

        var epilogue = _epilogueSteps.Count > 0
            ? _epilogueSteps.Select(s => s.Raw!).ToArray()
            : null;

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
            Sequences = grouped,
            Epilogue = epilogue
        };
    }

    private NpcLocation? InferAcceptFrom()
    {
        var acceptStep = _steps.FirstOrDefault(s => s.Raw is AcceptStep);
        return (acceptStep?.Raw as AcceptStep)?.Target;
    }
}
