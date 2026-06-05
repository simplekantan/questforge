namespace QuestForge.Engine.Authoring;

/// <summary>
/// Manages fragment draft lifecycle: load, cache, dirty tracking, auto-save, backup rotation.
/// Parallel to DraftManager but keyed by string (fragmentId) instead of QuestId.
/// </summary>
public sealed class FragmentDraftManager
{
    private readonly IFragmentDraftStorage _storage;
    private readonly IClock _clock;
    private readonly TimeSpan _autoSaveInterval;
    private readonly int _backupKeepCount;

    private readonly Dictionary<string, FragmentDraft> _cache = new();
    private readonly HashSet<string> _dirty = new();
    private readonly Dictionary<string, DateTimeOffset> _lastSavedAt = new();

    public FragmentDraftManager(
        IFragmentDraftStorage storage,
        IClock clock,
        TimeSpan autoSaveInterval = default,
        int backupKeepCount = 5)
    {
        _storage = storage;
        _clock = clock;
        _autoSaveInterval = autoSaveInterval == default ? TimeSpan.FromSeconds(60) : autoSaveInterval;
        _backupKeepCount = backupKeepCount;
    }

    public async Task<FragmentDraft> GetOrCreate(string fragmentId, CancellationToken ct)
    {
        if (_cache.TryGetValue(fragmentId, out var cached))
            return cached;

        var loadResult = await _storage.Load(fragmentId, ct);

        FragmentDraft draft;
        if (loadResult.IsSuccess && loadResult.ValueOrThrow is { } existing)
        {
            draft = existing;
        }
        else
        {
            draft = new FragmentDraft(fragmentId, _clock.UtcNow);
        }

        _cache[fragmentId] = draft;
        if (!_lastSavedAt.ContainsKey(fragmentId))
            _lastSavedAt[fragmentId] = _clock.UtcNow;
        return draft;
    }

    public async Task<FragmentDraft?> Get(string fragmentId, CancellationToken ct)
    {
        if (_cache.TryGetValue(fragmentId, out var cached))
            return cached;

        var loadResult = await _storage.Load(fragmentId, ct);
        if (loadResult.IsSuccess && loadResult.ValueOrThrow is { } existing)
        {
            _cache[fragmentId] = existing;
            return existing;
        }

        return null;
    }

    public IReadOnlyList<string> ActiveDraftIds => _cache.Keys.ToArray();

    public async Task SaveNow(string fragmentId, CancellationToken ct)
    {
        if (!_cache.TryGetValue(fragmentId, out var draft))
            return;

        await _storage.CreateBackup(fragmentId, ct);
        await _storage.Save(fragmentId, draft, ct);
        _dirty.Remove(fragmentId);
        _lastSavedAt[fragmentId] = _clock.UtcNow;
    }

    public async Task MaybeAutoSave(string fragmentId, CancellationToken ct)
    {
        if (!_dirty.Contains(fragmentId))
            return;

        var lastSaved = _lastSavedAt.TryGetValue(fragmentId, out var t) ? t : DateTimeOffset.MinValue;
        var elapsed = _clock.UtcNow - lastSaved;

        if (elapsed < _autoSaveInterval)
            return;

        await _storage.Save(fragmentId, _cache[fragmentId], ct);
        _dirty.Remove(fragmentId);
        _lastSavedAt[fragmentId] = _clock.UtcNow;
    }

    public async Task DiscardDraft(string fragmentId, CancellationToken ct)
    {
        await _storage.Delete(fragmentId, ct);
        _cache.Remove(fragmentId);
        _dirty.Remove(fragmentId);
    }

    public void MarkDirty(string fragmentId)
    {
        _dirty.Add(fragmentId);
    }
}
