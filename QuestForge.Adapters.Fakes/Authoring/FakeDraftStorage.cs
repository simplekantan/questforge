using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;

namespace QuestForge.Adapters.Fakes.Authoring;

/// <summary>
/// In-memory implementation of IDraftStorage for tests.
/// Maintains per-quest backup slots (max 5, most recent is slot 1).
/// </summary>
public sealed class FakeDraftStorage : IDraftStorage
{
    private readonly Dictionary<QuestId, QuestDraft> _drafts = new();

    // per-quest ordered list of backup "copies": index 0 = most recent (.bak.001)
    private readonly Dictionary<QuestId, List<QuestDraft>> _backups = new();

    public int SaveCount { get; private set; }

    public int BackupCount(QuestId questId) =>
        _backups.TryGetValue(questId, out var list) ? list.Count : 0;

    public Task<Result<bool>> Save(QuestId quest, QuestDraft draft, CancellationToken ct)
    {
        _drafts[quest] = draft;
        SaveCount++;
        return Task.FromResult<Result<bool>>(Result.Ok(true));
    }

    public Task<Result<QuestDraft?>> Load(QuestId quest, CancellationToken ct)
    {
        _drafts.TryGetValue(quest, out var draft);
        return Task.FromResult<Result<QuestDraft?>>(Result.Ok<QuestDraft?>(draft));
    }

    public Task<Result<IReadOnlyList<QuestId>>> ListDrafts(CancellationToken ct)
    {
        IReadOnlyList<QuestId> ids = _drafts.Keys.ToArray();
        return Task.FromResult<Result<IReadOnlyList<QuestId>>>(Result.Ok(ids));
    }

    public Task<Result<bool>> Delete(QuestId quest, CancellationToken ct)
    {
        _drafts.Remove(quest);
        _backups.Remove(quest);
        return Task.FromResult<Result<bool>>(Result.Ok(true));
    }

    public Task<Result<bool>> CreateBackup(QuestId quest, CancellationToken ct)
    {
        if (!_drafts.TryGetValue(quest, out var current))
            return Task.FromResult<Result<bool>>(Result.Ok(false));

        if (!_backups.TryGetValue(quest, out var slots))
        {
            slots = new List<QuestDraft>();
            _backups[quest] = slots;
        }

        // Shift: slot 5 is deleted, slot 4 → 5, ..., slot 1 → 2
        // List is ordered index 0 = most recent (.bak.001)
        const int maxBackups = 5;
        if (slots.Count >= maxBackups)
            slots.RemoveAt(slots.Count - 1);  // drop oldest (.bak.005 equivalent)

        // Insert current draft as new most-recent backup at front
        slots.Insert(0, current);

        return Task.FromResult<Result<bool>>(Result.Ok(true));
    }

    /// <summary>
    /// Returns the backup at slot index (0 = most recent = .bak.001).
    /// Returns null if no backup at that index.
    /// </summary>
    public QuestDraft? GetBackup(QuestId questId, int slotIndex)
    {
        if (!_backups.TryGetValue(questId, out var slots))
            return null;
        if (slotIndex < 0 || slotIndex >= slots.Count)
            return null;
        return slots[slotIndex];
    }
}
