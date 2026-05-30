using QuestForge.Adapters.Types;
using QuestForge.Schema;

namespace QuestForge.Adapters.Items;

public interface IItemUser
{
    Task<Result<Unit>> UseItem(
        ItemKind kind,
        uint itemId,
        NpcId? targetNpcId,
        Position3? targetPosition,
        CancellationToken ct);
}
