using QuestForge.Adapters.Types;
using QuestForge.Engine.Authoring;

namespace QuestForge.Adapters.Fakes.Authoring;

/// <summary>
/// In-memory implementation of IFragmentDraftStorage for tests.
/// Mirrors FakeDraftStorage but keyed by string (fragmentId).
/// </summary>
public sealed class FakeFragmentDraftStorage : IFragmentDraftStorage
{
    private readonly Dictionary<string, FragmentDraft> _drafts = new();
    private readonly Dictionary<string, List<FragmentDraft>> _backups = new();

    public int SaveCount { get; private set; }
    public string? LastSavedFragmentId { get; private set; }

    public Task<Result<bool>> Save(string fragmentId, FragmentDraft draft, CancellationToken ct)
    {
        _drafts[fragmentId] = draft;
        SaveCount++;
        LastSavedFragmentId = fragmentId;
        return Task.FromResult<Result<bool>>(Result.Ok(true));
    }

    public Task<Result<FragmentDraft?>> Load(string fragmentId, CancellationToken ct)
    {
        _drafts.TryGetValue(fragmentId, out var draft);
        return Task.FromResult<Result<FragmentDraft?>>(Result.Ok<FragmentDraft?>(draft));
    }

    public Task<Result<IReadOnlyList<string>>> ListDrafts(CancellationToken ct)
    {
        IReadOnlyList<string> ids = _drafts.Keys.ToArray();
        return Task.FromResult<Result<IReadOnlyList<string>>>(Result.Ok(ids));
    }

    public Task<Result<bool>> Delete(string fragmentId, CancellationToken ct)
    {
        _drafts.Remove(fragmentId);
        _backups.Remove(fragmentId);
        return Task.FromResult<Result<bool>>(Result.Ok(true));
    }

    public Task<Result<bool>> CreateBackup(string fragmentId, CancellationToken ct)
    {
        if (!_drafts.TryGetValue(fragmentId, out var current))
            return Task.FromResult<Result<bool>>(Result.Ok(false));

        if (!_backups.TryGetValue(fragmentId, out var slots))
        {
            slots = new List<FragmentDraft>();
            _backups[fragmentId] = slots;
        }

        const int maxBackups = 5;
        if (slots.Count >= maxBackups)
            slots.RemoveAt(slots.Count - 1);

        slots.Insert(0, current);

        return Task.FromResult<Result<bool>>(Result.Ok(true));
    }
}
