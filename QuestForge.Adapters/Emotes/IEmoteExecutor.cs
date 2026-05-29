using QuestForge.Adapters.Types;

namespace QuestForge.Adapters.Emotes;

public interface IEmoteExecutor
{
    Task<Result<Unit>> UseEmote(
        uint emoteId,
        NpcId? targetNpcId,
        bool motion,
        CancellationToken ct);
}
