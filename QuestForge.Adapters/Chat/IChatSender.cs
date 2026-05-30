using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Chat;

public interface IChatSender
{
    Task<Result<Unit>> Send(string message, NpcId? targetNpcId, CancellationToken ct);
}
