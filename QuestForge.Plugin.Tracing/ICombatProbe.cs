namespace QuestForge.Plugin.Tracing;

public interface ICombatProbe
{
    bool IsInCombat();
    IReadOnlyList<(ulong ObjectId, uint DataId)> GetVisibleHostiles();
}
