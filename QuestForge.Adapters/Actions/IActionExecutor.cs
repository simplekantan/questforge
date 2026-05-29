using QuestForge.Adapters.Types;
using QuestForge.Schema;

namespace QuestForge.Adapters.Actions;

/// <summary>
/// Fires a single game action (combat ability, general action, key item) on an optional target.
/// </summary>
public interface IActionExecutor
{
    Task<Result<Unit>> UseAction(
        ActionType type,
        uint actionId,
        NpcId? targetNpcId,
        CancellationToken ct);

    Task<Result<ActionStatus>> GetActionStatus(
        ActionType type,
        uint actionId,
        CancellationToken ct);
}
