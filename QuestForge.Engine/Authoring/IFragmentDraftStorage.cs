using QuestForge.Adapters.Types;

namespace QuestForge.Engine.Authoring;

/// <summary>
/// Storage abstraction for fragment drafts. Parallel to IDraftStorage but keyed by string fragmentId.
/// </summary>
public interface IFragmentDraftStorage
{
    Task<Result<bool>> Save(string fragmentId, FragmentDraft draft, CancellationToken ct);
    Task<Result<FragmentDraft?>> Load(string fragmentId, CancellationToken ct);
    Task<Result<IReadOnlyList<string>>> ListDrafts(CancellationToken ct);
    Task<Result<bool>> Delete(string fragmentId, CancellationToken ct);

    /// <summary>
    /// Copy current draft file to .bak.001 (rotating older backups).
    /// Keeps the most recent 5 backups; deletes .bak.006 and beyond.
    /// </summary>
    Task<Result<bool>> CreateBackup(string fragmentId, CancellationToken ct);
}
