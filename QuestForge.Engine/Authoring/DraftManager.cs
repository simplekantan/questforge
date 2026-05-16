using QuestForge.Adapters.Types;

namespace QuestForge.Engine.Authoring;

public sealed class DraftManager
{
    private readonly IDraftStorage _storage;
    private readonly IClock _clock;
    private readonly TimeSpan _autoSaveInterval;
    private readonly int _backupKeepCount;

    private readonly Dictionary<QuestId, QuestDraft> _cache = new();
    private readonly HashSet<QuestId> _dirty = new();
    private readonly Dictionary<QuestId, DateTimeOffset> _lastSavedAt = new();

    public DraftManager(
        IDraftStorage storage,
        IClock clock,
        TimeSpan autoSaveInterval = default,
        int backupKeepCount = 5)
    {
        _storage = storage;
        _clock = clock;
        _autoSaveInterval = autoSaveInterval == default ? TimeSpan.FromSeconds(60) : autoSaveInterval;
        _backupKeepCount = backupKeepCount;
    }

    /// <summary>
    /// Load from storage if a draft exists; otherwise create a new empty draft.
    /// If storage returns a Failure (I/O error), treat as not-found and create a new draft.
    /// The new draft is NOT automatically persisted — call SaveNow explicitly.
    /// </summary>
    public async Task<QuestDraft> GetOrCreate(QuestId quest, CancellationToken ct)
    {
        if (_cache.TryGetValue(quest, out var cached))
            return cached;

        var loadResult = await _storage.Load(quest, ct);

        QuestDraft draft;
        if (loadResult.IsSuccess && loadResult.ValueOrThrow is { } existing)
        {
            draft = existing;
        }
        else
        {
            draft = new QuestDraft(quest, _clock.UtcNow);
        }

        _cache[quest] = draft;
        // Initialize lastSavedAt to "now" so that MaybeAutoSave respects the interval
        // from the point the draft was loaded/created, not from DateTimeOffset.MinValue.
        if (!_lastSavedAt.ContainsKey(quest))
            _lastSavedAt[quest] = _clock.UtcNow;
        return draft;
    }

    public async Task<QuestDraft?> Get(QuestId quest, CancellationToken ct)
    {
        if (_cache.TryGetValue(quest, out var cached))
            return cached;

        var loadResult = await _storage.Load(quest, ct);
        if (loadResult.IsSuccess && loadResult.ValueOrThrow is { } existing)
        {
            _cache[quest] = existing;
            return existing;
        }

        return null;
    }

    public IReadOnlyList<QuestId> ActiveDraftIds => _cache.Keys.ToArray();

    /// <summary>Persist draft and rotate backups.</summary>
    /// <remarks>Not thread-safe. Expected to be invoked from the Dalamud framework thread only.</remarks>
    public async Task SaveNow(QuestId quest, CancellationToken ct)
    {
        if (!_cache.TryGetValue(quest, out var draft))
            return;

        // WHY: backup before save so the backup reflects the previous on-disk state,
        // not the version we're about to write. If no file exists yet, CreateBackup
        // returns Ok(false) as a no-op — this is intentional and not an error.
        await _storage.CreateBackup(quest, ct);
        await _storage.Save(quest, draft, ct);
        _dirty.Remove(quest);
        _lastSavedAt[quest] = _clock.UtcNow;
    }

    /// <summary>Save if dirty AND elapsed >= autoSaveInterval. No-op otherwise.</summary>
    /// <remarks>
    /// WHY: auto-save is best-effort persistence only — no backup rotation.
    /// Backups rotate only on explicit SaveNow (user-initiated or export).
    /// Frequent auto-saves that rotate backups would exhaust the 5-slot history.
    /// </remarks>
    public async Task MaybeAutoSave(QuestId quest, CancellationToken ct)
    {
        if (!_dirty.Contains(quest))
            return;

        var lastSaved = _lastSavedAt.TryGetValue(quest, out var t) ? t : DateTimeOffset.MinValue;
        var elapsed = _clock.UtcNow - lastSaved;

        if (elapsed < _autoSaveInterval)
            return;

        await _storage.Save(quest, _cache[quest], ct);
        _dirty.Remove(quest);
        _lastSavedAt[quest] = _clock.UtcNow;
    }

    public async Task DiscardDraft(QuestId quest, CancellationToken ct)
    {
        await _storage.Delete(quest, ct);
        _cache.Remove(quest);
        _dirty.Remove(quest);
    }

    /// <summary>Mark dirty — called by AuthoringHost after every mutation.</summary>
    public void MarkDirty(QuestId quest)
    {
        _dirty.Add(quest);
    }
}
