using QuestForge.Adapters.Types;

namespace QuestForge.Engine.Authoring;

public interface IDraftStorage
{
    Task<Result<bool>> Save(QuestId quest, QuestDraft draft, CancellationToken ct);
    Task<Result<QuestDraft?>> Load(QuestId quest, CancellationToken ct);
    Task<Result<IReadOnlyList<QuestId>>> ListDrafts(CancellationToken ct);
    Task<Result<bool>> Delete(QuestId quest, CancellationToken ct);

    /// <summary>
    /// Copy current draft file to .bak.001 (rotating older backups).
    /// Keeps the most recent 5 backups; deletes .bak.006 and beyond.
    /// Rotation: before writing the new .bak.001, shift existing backups
    /// (.bak.001 → .bak.002, .bak.002 → .bak.003, ..., .bak.005 → deleted).
    /// </summary>
    Task<Result<bool>> CreateBackup(QuestId quest, CancellationToken ct);
}
