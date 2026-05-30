using QuestForge.Adapters.Types;
using QuestForge.Schema;

namespace QuestForge.Adapters.Items;

public enum ItemTargetMode { Direct, Npc, Ground }

public static class ItemUseInterpreter
{
    /// <summary>Direct (no target), Npc (TargetManager target), or Ground (UseActionLocation).
    /// Throws ArgumentException if BOTH targets are set (validator E15 rejects this at author
    /// time; this is defense-in-depth).</summary>
    public static ItemTargetMode ResolveTargetMode(NpcId? targetNpcId, Position3? targetPosition)
    {
        if (targetNpcId is not null && targetPosition is not null)
            throw new ArgumentException(
                "UseItem cannot target both an NPC and a ground position.", nameof(targetNpcId));
        if (targetNpcId is not null) return ItemTargetMode.Npc;
        if (targetPosition is not null) return ItemTargetMode.Ground;
        return ItemTargetMode.Direct;
    }
}
