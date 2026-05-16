using QuestForge.Adapters.Types;

namespace QuestForge.Engine.Authoring;

public sealed class DraftManager
{
    public DraftManager(
        IDraftStorage storage,
        IClock clock,
        TimeSpan autoSaveInterval = default,
        int backupKeepCount = 5)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Load from storage if a draft exists; otherwise create a new empty draft.
    /// If storage returns a Failure (I/O error), treat as not-found and create a new draft.
    /// The new draft is NOT automatically persisted — call SaveNow explicitly.
    /// </summary>
    public Task<QuestDraft> GetOrCreate(QuestId quest, CancellationToken ct)
    {
        throw new NotImplementedException();
    }

    public Task<QuestDraft?> Get(QuestId quest, CancellationToken ct)
    {
        throw new NotImplementedException();
    }

    public IReadOnlyList<QuestId> ActiveDraftIds
    {
        get => throw new NotImplementedException();
    }

    /// <summary>Persist draft and rotate backups.</summary>
    public Task SaveNow(QuestId quest, CancellationToken ct)
    {
        throw new NotImplementedException();
    }

    /// <summary>Save if dirty AND elapsed >= autoSaveInterval. No-op otherwise.</summary>
    public Task MaybeAutoSave(QuestId quest, CancellationToken ct)
    {
        throw new NotImplementedException();
    }

    public Task DiscardDraft(QuestId quest, CancellationToken ct)
    {
        throw new NotImplementedException();
    }

    /// <summary>Mark dirty — called by AuthoringHost after every mutation.</summary>
    public void MarkDirty(QuestId quest)
    {
        throw new NotImplementedException();
    }
}
