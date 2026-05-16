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
        throw new NotImplementedException();
    }

    public bool RemoveStep(string stepId, DateTimeOffset now)
    {
        throw new NotImplementedException();
    }

    public bool ReplaceStep(string stepId, DraftStep newStep, DateTimeOffset now)
    {
        throw new NotImplementedException();
    }

    public DraftStep? GetStep(string stepId)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Convert draft to a schema-valid QuestDefinition.
    /// Throws DraftSerializationException if any step lacks a Raw value.
    /// Steps are grouped into QuestSequence objects by SequenceNumber, ascending.
    /// </summary>
    public QuestDefinition ToQuestDefinition()
    {
        throw new NotImplementedException();
    }
}
